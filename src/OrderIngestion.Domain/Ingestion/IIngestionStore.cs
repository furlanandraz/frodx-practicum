using OrderIngestion.Domain.Orders;

namespace OrderIngestion.Domain.Ingestion;

/// <summary>Persistence for synced orders and the cursor.</summary>
public interface IIngestionStore
{
    /// <summary>The cursor, or <see cref="IngestionCursor.Empty"/> before the first cycle.</summary>
    Task<IngestionCursor> GetCursorAsync(CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction: stores the orders that aren't stored yet (by canonical hash), all stamped
    /// with <paramref name="batchTimestamp"/>, and saves the cursor. Either all of it is saved or none
    /// of it, so the cursor never moves past orders that weren't saved.
    /// </summary>
    /// <returns>How many orders were new.</returns>
    Task<int> SaveBatchAsync(
        DateTimeOffset batchTimestamp,
        IReadOnlyList<Order> orders,
        IngestionCursor cursor,
        CancellationToken cancellationToken);

    /// <summary>Saves only the cursor: marking a cycle as started or failed.</summary>
    Task SaveCursorAsync(IngestionCursor cursor, CancellationToken cancellationToken);
}
