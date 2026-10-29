using OrderIngestion.Domain.Orders;
using OrderIngestion.Infrastructure.Orders;
using OrderIngestion.Tests.TestSupport;

namespace OrderIngestion.Tests.Orders;

/// <summary>Any response that doesn't match our schema exactly is schema drift, and nothing from it is used.</summary>
public sealed class OrderPayloadReaderTests
{
    [Fact]
    public void Matching_response_becomes_orders_with_their_raw_payload()
    {
        var orders = OrderPayloadReader.Read($"[{Sample.OrderJson(id: "ORD-1")}]");

        var order = Assert.Single(orders);
        Assert.Equal("ORD-1", order.ExternalId);
        Assert.Contains("\"ORD-1\"", order.Payload);
    }

    [Fact]
    public void Empty_list_is_fine()
    {
        Assert.Empty(OrderPayloadReader.Read("[]"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("<html>502 Bad Gateway</html>")]
    [InlineData("""{ "orders": [] }""")]
    public void Response_that_is_not_a_list_of_orders_is_drift(string body)
    {
        AssertDrift(body);
    }

    [Fact]
    public void Added_field_is_drift()
    {
        AssertDrift($"[{Sample.OrderJson().Replace("\"currency\"", "\"channel\": \"web\", \"currency\"")}]", "channel");
    }

    [Fact]
    public void Missing_field_is_drift()
    {
        AssertDrift($"[{Sample.OrderJson().Replace("\"totalAmount\": 10.00,", "")}]", "totalAmount");
    }

    [Fact]
    public void Null_in_a_required_field_is_drift()
    {
        AssertDrift($"[{Sample.OrderJson().Replace("\"CUST-1\"", "null")}]", "customerId");
    }

    [Fact]
    public void Number_sent_as_string_is_drift()
    {
        AssertDrift($"[{Sample.OrderJson().Replace("\"totalAmount\": 10.00", "\"totalAmount\": \"10.00\"")}]", "totalAmount");
    }

    [Theory]
    [InlineData("3")]        // Enum.TryParse would accept this
    [InlineData("on_hold")]
    public void Unknown_status_is_drift(string status)
    {
        AssertDrift($"[{Sample.OrderJson(status: status)}]", status);
    }

    [Fact]
    public void One_bad_record_makes_the_whole_response_drift()
    {
        var body = $"[{Sample.OrderJson(id: "ORD-GOOD")}, {Sample.OrderJson(id: "ORD-BAD", status: "nope")}]";

        AssertDrift(body, "Order #1 (ORD-BAD)");
    }

    private static void AssertDrift(string body, string? mentions = null)
    {
        var ex = Assert.Throws<OrderSourceException>(() => OrderPayloadReader.Read(body));

        Assert.Equal(OrderSourceFailure.SchemaDrift, ex.Failure);
        if (mentions is not null)
        {
            Assert.Contains(mentions, ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
