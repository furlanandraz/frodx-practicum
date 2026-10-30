using OrderIngestion.Domain.Orders;

namespace OrderIngestion.Domain.Ingestion;

/// <summary>Summary of one successful cycle. The host logs it.</summary>
/// <param name="StartedAt">Also the batch timestamp written to every row of this cycle.</param>
/// <param name="Since">The lower bound the source was asked for; null on the first cycle.</param>
/// <param name="InterruptedRunStartedAt">Set when the previous cycle died before finishing: when it started.</param>
/// <param name="Fetched">Every order received, valid or skipped.</param>
/// <param name="Duplicates">Valid orders that were already stored (or repeated within the response).</param>
/// <param name="Skipped">Orders that failed validation: logged, not stored.</param>
public sealed record IngestionResult(
    Guid RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    DateTimeOffset? Since,
    DateTimeOffset? InterruptedRunStartedAt,
    int Fetched,
    int Inserted,
    int Duplicates,
    IReadOnlyList<SkippedOrder> Skipped)
{
    public TimeSpan Duration => FinishedAt - StartedAt;
}
