using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderIngestion.Domain.Orders;
using OrderIngestion.Infrastructure;
using OrderIngestion.Tests.TestSupport;

namespace OrderIngestion.Tests.Orders;

/// <summary>
/// Runs the real HttpClient registration against a stub handler: one attempt per cycle,
/// and every failure sorted into the right kind.
/// </summary>
public sealed class HttpOrderSourceTests
{
    [Fact]
    public async Task Ok_response_returns_the_orders()
    {
        var handler = new StubHandler(_ => Task.FromResult(OkResponse()));

        var result = await CreateSource(handler).FetchOrdersAsync(since: null, CancellationToken.None);

        Assert.Single(result.Orders);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, OrderSourceFailure.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized, OrderSourceFailure.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable, OrderSourceFailure.Other)]
    [InlineData(HttpStatusCode.InternalServerError, OrderSourceFailure.Other)]
    [InlineData(HttpStatusCode.Forbidden, OrderSourceFailure.Other)]
    [InlineData(HttpStatusCode.BadRequest, OrderSourceFailure.Other)]
    public async Task Error_status_is_one_attempt_sorted_into_its_kind(HttpStatusCode status, OrderSourceFailure expected)
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(status)));

        var ex = await Assert.ThrowsAsync<OrderSourceException>(
            () => CreateSource(handler).FetchOrdersAsync(since: null, CancellationToken.None));

        Assert.Equal(expected, ex.Failure);
        Assert.Equal(1, handler.Calls); // no retries within a cycle
    }

    [Fact]
    public async Task Network_error_is_other_and_not_retried()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));

        var ex = await Assert.ThrowsAsync<OrderSourceException>(
            () => CreateSource(handler).FetchOrdersAsync(since: null, CancellationToken.None));

        Assert.Equal(OrderSourceFailure.Other, ex.Failure);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Timeout_is_other_and_not_retried()
    {
        // Hangs past the 1 s timeout configured below.
        var handler = new StubHandler(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return OkResponse();
        });

        var ex = await Assert.ThrowsAsync<OrderSourceException>(
            () => CreateSource(handler).FetchOrdersAsync(since: null, CancellationToken.None));

        Assert.Equal(OrderSourceFailure.Other, ex.Failure);
        Assert.Contains("did not answer", ex.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Response_that_does_not_match_our_schema_is_drift()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{ "orders": [] }""", Encoding.UTF8, "application/json"),
        }));

        var ex = await Assert.ThrowsAsync<OrderSourceException>(
            () => CreateSource(handler).FetchOrdersAsync(since: null, CancellationToken.None));

        Assert.Equal(OrderSourceFailure.SchemaDrift, ex.Failure);
    }

    [Fact]
    public async Task Since_is_sent_as_an_iso_query_parameter()
    {
        var handler = new StubHandler(_ => Task.FromResult(OkResponse()));

        await CreateSource(handler).FetchOrdersAsync(Sample.At("2026-10-29T10:00:00Z"), CancellationToken.None);

        Assert.Equal("?since=2026-10-29T10%3A00%3A00.0000000Z", handler.LastRequestUri?.Query);
    }

    [Fact]
    public async Task Since_is_added_after_a_query_string_already_in_the_path()
    {
        var handler = new StubHandler(_ => Task.FromResult(OkResponse()));

        await CreateSource(handler, ordersPath: "/orders?limit=100")
            .FetchOrdersAsync(Sample.At("2026-10-29T10:00:00Z"), CancellationToken.None);

        Assert.Equal("?limit=100&since=2026-10-29T10%3A00%3A00.0000000Z", handler.LastRequestUri?.Query);
    }

    private static IOrderSource CreateSource(StubHandler handler, string ordersPath = "/orders")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OrderApi:BaseUrl"] = "http://upstream.test",
                ["OrderApi:OrdersPath"] = ordersPath,
                ["OrderApi:TimeoutSeconds"] = "1",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddInfrastructure(configuration);
        services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(() => handler));

        return services.BuildServiceProvider().GetRequiredService<IOrderSource>();
    }

    private static HttpResponseMessage OkResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"[{Sample.OrderJson()}]", Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequestUri = request.RequestUri;
            return respond(cancellationToken);
        }
    }
}
