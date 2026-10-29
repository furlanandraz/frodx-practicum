using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;
using OrderIngestion.Domain.Orders;

namespace OrderIngestion.Infrastructure.Orders;

/// <summary>
/// Fetches orders from the upstream HTTP API: one attempt per cycle, no retries within it.
/// A failed attempt is retried by the next cycle, which starts from the same cursor.
/// </summary>
internal sealed class HttpOrderSource(HttpClient httpClient, IOptions<OrderApiOptions> options) : IOrderSource
{
    public async Task<IReadOnlyList<Order>> FetchOrdersAsync(DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var path = BuildPath(options.Value.OrdersPath, since);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.GetAsync(path, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new OrderSourceException(OrderSourceFailure.Other, $"Network error calling the order API ({path}): {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout elapsed (a real shutdown would have cancelled our own token instead).
            throw new OrderSourceException(OrderSourceFailure.Other, $"Order API did not answer within {httpClient.Timeout.TotalSeconds:F0} s ({path})", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var failure = response.StatusCode switch
                {
                    HttpStatusCode.NotFound => OrderSourceFailure.NotFound,
                    HttpStatusCode.Unauthorized => OrderSourceFailure.Unauthorized,
                    _ => OrderSourceFailure.Other,
                };

                throw new OrderSourceException(
                    failure, $"Order API returned {(int)response.StatusCode} {response.ReasonPhrase} for {path}");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return OrderPayloadReader.Read(body);
        }
    }

    private static string BuildPath(string ordersPath, DateTimeOffset? since) =>
        since is null
            ? ordersPath
            : $"{ordersPath}?since={Uri.EscapeDataString(since.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}";
}
