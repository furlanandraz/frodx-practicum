using OrderIngestion.Domain.Orders;
using OrderIngestion.Infrastructure.Orders;
using OrderIngestion.Tests.TestSupport;

namespace OrderIngestion.Tests.Orders;

/// <summary>
/// Shape problems make the whole response schema drift. Value problems skip only that order.
/// </summary>
public sealed class OrderPayloadReaderTests
{
    [Fact]
    public void Matching_response_becomes_orders_with_their_raw_payload()
    {
        var result = OrderPayloadReader.Read($"[{Sample.OrderJson(id: "ORD-1")}]");

        var order = Assert.Single(result.Orders);
        Assert.Equal("ORD-1", order.ExternalId);
        Assert.Contains("\"ORD-1\"", order.Payload);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Empty_list_is_fine()
    {
        Assert.Empty(OrderPayloadReader.Read("[]").Orders);
    }

    // --- Shape: schema drift ------------------------------------------------

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
    public void Number_sent_as_string_is_drift()
    {
        AssertDrift($"[{Sample.OrderJson().Replace("\"totalAmount\": 10.00", "\"totalAmount\": \"10.00\"")}]", "totalAmount");
    }

    [Fact]
    public void One_order_with_the_wrong_shape_makes_the_whole_response_drift()
    {
        var bad = Sample.OrderJson(id: "ORD-BAD").Replace("\"currency\"", "\"channel\": \"web\", \"currency\"");

        AssertDrift($"[{Sample.OrderJson(id: "ORD-GOOD")}, {bad}]", "Order #1 (ORD-BAD)");
    }

    // --- Values: that order is skipped, the rest kept -----------------------

    [Theory]
    [InlineData("\"customerId\": \"CUST-1\"", "\"customerId\": null", "customerId is null")]
    [InlineData("\"totalAmount\": 10.00", "\"totalAmount\": null", "totalAmount is null")]
    [InlineData("\"createdAt\": \"2026-10-29T10:00:00Z\"", "\"createdAt\": null", "createdAt is null")]
    [InlineData("\"lines\": [ { \"sku\": \"SKU-1\", \"quantity\": 1, \"unitPrice\": 10.00 } ]", "\"lines\": null", "lines is null")]
    [InlineData("\"lines\": [ { \"sku\": \"SKU-1\", \"quantity\": 1, \"unitPrice\": 10.00 } ]", "\"lines\": [ null ]", "lines[0] is null")]
    [InlineData("\"quantity\": 1", "\"quantity\": null", "lines[0].quantity is null")]
    public void Null_value_skips_that_order(string field, string withNull, string reason)
    {
        AssertSkipped(Sample.OrderJson().Replace(field, withNull), reason);
    }

    [Fact]
    public void Null_order_skips_that_order()
    {
        var result = OrderPayloadReader.Read($"[{Sample.OrderJson()}, null]");

        Assert.Single(result.Orders);
        Assert.Equal(new SkippedOrder(1, null, "order is null"), Assert.Single(result.Skipped));
    }

    [Fact]
    public void Null_is_checked_before_the_shape()
    {
        // An order with a null and an added field is skipped for the null, not treated as drift.
        var order = Sample.OrderJson().Replace("\"CUST-1\"", "null").Replace("\"currency\"", "\"channel\": \"web\", \"currency\"");

        AssertSkipped(order, "customerId is null");
    }

    [Theory]
    [InlineData("3")]        // Enum.TryParse would accept this
    [InlineData("on_hold")]
    public void Unknown_status_skips_that_order(string status)
    {
        AssertSkipped(Sample.OrderJson(status: status), status);
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00Z")]
    [InlineData("2025-06-01T12:00:00Z")]
    public void Old_orders_are_accepted(string createdAt)
    {
        // The 2026 floor applies to the cursor, not to orders (see OrderIngestionService).
        var result = OrderPayloadReader.Read($"[{Sample.OrderJson().Replace("2026-10-29T10:00:00Z", createdAt)}]");

        Assert.Single(result.Orders);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Non_positive_quantity_skips_that_order()
    {
        AssertSkipped(Sample.OrderJson().Replace("\"quantity\": 1", "\"quantity\": 0"), "quantity");
    }

    [Fact]
    public void Skipped_order_does_not_affect_the_others()
    {
        var body = $"[{Sample.OrderJson(id: "ORD-1")}, {Sample.OrderJson(id: "ORD-2").Replace("\"CUST-1\"", "null")}, {Sample.OrderJson(id: "ORD-3")}]";

        var result = OrderPayloadReader.Read(body);

        Assert.Equal(["ORD-1", "ORD-3"], result.Orders.Select(o => o.ExternalId));
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal(new SkippedOrder(1, "ORD-2", "customerId is null"), skipped);
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

    private static void AssertSkipped(string orderJson, string reason)
    {
        var result = OrderPayloadReader.Read($"[{orderJson}]");

        Assert.Empty(result.Orders);
        Assert.Contains(reason, Assert.Single(result.Skipped).Reason, StringComparison.OrdinalIgnoreCase);
    }
}
