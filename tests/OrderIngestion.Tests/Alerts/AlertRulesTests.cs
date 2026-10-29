using OrderIngestion.Domain.Alerts;
using OrderIngestion.Domain.Orders;

namespace OrderIngestion.Tests.Alerts;

/// <summary>Emails for exactly three failures: 404, 401 and schema drift.</summary>
public sealed class AlertRulesTests
{
    [Theory]
    [InlineData(OrderSourceFailure.NotFound, "api-not-found")]
    [InlineData(OrderSourceFailure.Unauthorized, "api-unauthorized")]
    [InlineData(OrderSourceFailure.SchemaDrift, "schema-drift")]
    public void Failures_a_person_must_fix_are_emailed(OrderSourceFailure failure, string expectedKey)
    {
        var alert = AlertRules.ForFailure(new OrderSourceException(failure, "details"), Guid.NewGuid());

        Assert.Equal(expectedKey, alert?.Key);
        Assert.Contains("details", alert?.Body);
    }

    [Fact]
    public void Other_failures_are_only_logged()
    {
        Assert.Null(AlertRules.ForFailure(new OrderSourceException(OrderSourceFailure.Other, "503"), Guid.NewGuid()));
    }

    [Fact]
    public void Unexpected_exceptions_are_only_logged()
    {
        Assert.Null(AlertRules.ForFailure(new InvalidOperationException("database is locked"), Guid.NewGuid()));
    }
}
