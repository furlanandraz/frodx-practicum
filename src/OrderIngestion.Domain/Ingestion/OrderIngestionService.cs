using OrderIngestion.Domain.Orders;

namespace OrderIngestion.Domain.Ingestion;

/// <summary>
/// One cycle: read the cursor → mark the cycle as running → fetch orders since the cursor →
/// drop duplicates → save the new orders and the new cursor in one transaction.
/// </summary>
/// <remarks>
/// Any failure (including schema drift) is an early return: nothing is written except the cursor's
/// "failed" status, and the read position stays where it was. The next cycle fetches the same window
/// again; that is the retry. Stored orders are de-duplicated by hash, so fetching twice is harmless.
/// <para>
/// Orders skipped by validation are not a failure: the rest of the batch is stored, and the read
/// position moves past them like past any other order.
/// </para>
/// <para>
/// If the process dies mid-cycle, the same applies: the position only moves in the transaction that
/// stores the orders. The cycle leaves <see cref="RunStatus.Running"/> behind, which the next one reports.
/// </para>
/// <para>
/// Does no logging or I/O of its own: it talks to the outside only through ports
/// (<see cref="IOrderSource"/>, <see cref="IIngestionStore"/>, <see cref="TimeProvider"/>),
/// so it can be unit-tested with fakes. The host logs the result and sends emails.
/// </para>
/// </remarks>
public sealed class OrderIngestionService(
    IOrderSource source,
    IIngestionStore store,
    IngestionSettings settings,
    TimeProvider timeProvider)
{
    // The cursor never reads from before this date. Orders may be older (the first cycle fetches
    // everything), but the read position doesn't go back further. It also keeps "youngest − overlap"
    // far away from DateTimeOffset.MinValue, where the subtraction would throw.
    private static readonly DateTimeOffset CursorFloor = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public async Task<IngestionResult> RunAsync(Guid runId, CancellationToken cancellationToken)
    {
        // Also the batch timestamp: every row this cycle writes carries it.
        var startedAt = timeProvider.GetUtcNow();

        var cursor = await store.GetCursorAsync(cancellationToken);
        var since = ReadFrom(cursor.YoungestRecord);

        // Still "running" from last time means that cycle never finished.
        var interruptedRunStartedAt = cursor.LastRunStatus == RunStatus.Running ? cursor.LastRun : null;

        // Mark this cycle as started. The read position does not change here.
        await store.SaveCursorAsync(
            cursor with { LastRun = startedAt, LastRunStatus = RunStatus.Running },
            cancellationToken);

        try
        {
            var fetched = await source.FetchOrdersAsync(since, cancellationToken);

            // The database's unique index is the real guarantee; this just avoids
            // sending an order twice when the response itself repeats it.
            var unique = fetched.Orders.DistinctBy(order => order.CanonicalHash).ToList();

            var nextCursor = cursor with
            {
                LastRun = startedAt,
                LastRunStatus = RunStatus.Succeeded,
                YoungestRecord = Youngest(cursor.YoungestRecord, unique),
            };

            var inserted = await store.SaveBatchAsync(startedAt, unique, nextCursor, cancellationToken);

            return new IngestionResult(
                runId,
                startedAt,
                FinishedAt: timeProvider.GetUtcNow(),
                since,
                interruptedRunStartedAt,
                Fetched: fetched.Orders.Count + fetched.Skipped.Count,
                Inserted: inserted,
                Duplicates: fetched.Orders.Count - inserted,
                fetched.Skipped);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TryMarkFailedAsync(cursor, startedAt);
            throw;
        }
    }

    private async Task TryMarkFailedAsync(IngestionCursor cursor, DateTimeOffset startedAt)
    {
        try
        {
            await store.SaveCursorAsync(
                cursor with { LastRun = startedAt, LastRunStatus = RunStatus.Failed },
                CancellationToken.None);
        }
        catch
        {
            // Best effort: if the database is what failed, this fails too. The original
            // error is the one that matters and is rethrown by the caller.
        }
    }

    // Where to start reading: the newest stored order minus the overlap, but never before the floor.
    // Null before anything is stored: the first cycle fetches everything.
    private DateTimeOffset? ReadFrom(DateTimeOffset? youngest)
    {
        if (youngest is null)
        {
            return null;
        }

        var position = youngest < CursorFloor ? CursorFloor : youngest.Value;
        return position - settings.CursorOverlap;
    }

    private static DateTimeOffset? Youngest(DateTimeOffset? current, IReadOnlyList<Order> orders)
    {
        if (orders.Count == 0)
        {
            return current;
        }

        var youngestFetched = orders.Max(order => order.CreatedAt);
        return current is null || youngestFetched > current ? youngestFetched : current;
    }
}
