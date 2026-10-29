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
        var orders = OrderPayloadReader.Read(Sample.Scenario(scenario));

        Assert.Equal(expected, orders.Count);
        Assert.Equal(expected, orders.Select(o => o.CanonicalHash).Distinct().Count());
    }

    [Fact]
    public void Default_scenario_totals_match_their_lines()
    {
        foreach (var order in OrderPayloadReader.Read(Sample.Scenario("orders")))
        {
            var linesTotal = order.Lines.Sum(line => line.Quantity * line.UnitPrice);
            Assert.True(linesTotal == order.TotalAmount,
                $"{order.ExternalId}: lines add up to {linesTotal}, total says {order.TotalAmount}");
        }
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
