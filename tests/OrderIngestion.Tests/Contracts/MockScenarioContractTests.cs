using OrderIngestion.Domain.Orders;
using OrderIngestion.Infrastructure.Orders;
using OrderIngestion.Tests.TestSupport;

namespace OrderIngestion.Tests.Contracts;

/// <summary>
/// Reads the exact files the mock API serves, through the worker's real reader, so the
/// scenarios and our schema cannot drift apart unnoticed.
/// </summary>
public sealed class MockScenarioContractTests
{
    [Theory]
    [InlineData("orders", 5)]
    [InlineData("orders-new", 6)]
    public void Valid_scenarios_match_our_schema(string scenario, int expected)
    {
        var result = OrderPayloadReader.Read(Sample.Scenario(scenario));

        Assert.Equal(expected, result.Orders.Count);
        Assert.Empty(result.Skipped);
        Assert.Equal(expected, result.Orders.Select(o => o.CanonicalHash).Distinct().Count());
    }

    [Fact]
    public void Default_scenario_totals_match_their_lines()
    {
        foreach (var order in OrderPayloadReader.Read(Sample.Scenario("orders")).Orders)
        {
            var linesTotal = order.Lines.Sum(line => line.Quantity * line.UnitPrice);
            Assert.True(linesTotal == order.TotalAmount,
                $"{order.ExternalId}: lines add up to {linesTotal}, total says {order.TotalAmount}");
        }
    }

    [Fact]
    public void Invalid_values_skip_only_those_orders()
    {
        var result = OrderPayloadReader.Read(Sample.Scenario("orders-invalid"));

        Assert.Equal(["ORD-1011"], result.Orders.Select(o => o.ExternalId));
        Assert.Equal(
            [new SkippedOrder(1, "ORD-1012", "customerId is null"), new SkippedOrder(2, "ORD-1013", "lines[0] is null")],
            result.Skipped);
    }

    [Fact]
    public void Added_fields_are_schema_drift()
    {
        var ex = Assert.Throws<OrderSourceException>(() => OrderPayloadReader.Read(Sample.Scenario("orders-drift")));

        Assert.Equal(OrderSourceFailure.SchemaDrift, ex.Failure);
        Assert.Contains("channel", ex.Message);
    }

    [Fact]
    public void Broken_records_are_schema_drift_naming_the_first_bad_one()
    {
        var ex = Assert.Throws<OrderSourceException>(() => OrderPayloadReader.Read(Sample.Scenario("orders-broken")));

        Assert.Equal(OrderSourceFailure.SchemaDrift, ex.Failure);
        Assert.Contains("Order #1 (ORD-1008)", ex.Message);
        Assert.Contains("totalAmount", ex.Message);
    }
}
