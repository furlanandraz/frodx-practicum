using OrderIngestion.Domain.Orders;

namespace OrderIngestion.Domain.Alerts;

/// <summary>
/// Emails go out for exactly three failures, all of which need a person: the endpoint is gone (404),
/// our credentials are refused (401), or the response no longer matches our schema. Everything else
/// is only logged, and the next cycle tries again.
/// </summary>
public static class AlertRules
{
    /// <returns>The email to send, or null when the failure only needs logging.</returns>
    public static Alert? ForFailure(Exception exception, Guid runId) => exception switch
    {
        OrderSourceException { Failure: OrderSourceFailure.NotFound } e => new Alert(
            "api-not-found",
            "Order API returned 404",
            $"""
            {e.Message}

            The endpoint is wrong or no longer exists on the API side.
            Check OrderApi:BaseUrl and OrderApi:OrdersPath, or ask the API owner.

            {Footer(runId)}
            """),

        OrderSourceException { Failure: OrderSourceFailure.Unauthorized } e => new Alert(
            "api-unauthorized",
            "Order API refused our credentials (401)",
            $"""
            {e.Message}

            Our credentials are refused, possibly because they are stale. Fix them on our side.

            {Footer(runId)}
            """),

        OrderSourceException { Failure: OrderSourceFailure.SchemaDrift } e => new Alert(
            "schema-drift",
            "Order API response no longer matches our schema",
            $"""
            {e.Message}

            Nothing from this response was stored, and the cursor did not move: once our model
            (OrderDto) or their data is fixed, the next cycle picks up the same orders.

            {Footer(runId)}
            """),

        _ => null,
    };

    private static string Footer(Guid runId) =>
        $"Run {runId}. Search the worker log for this RunId for details.";
}
