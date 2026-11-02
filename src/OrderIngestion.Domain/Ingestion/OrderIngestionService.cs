using OrderIngestion.Domain.Orders;

namespace OrderIngestion.Domain.Ingestion;

/// <summary>
/// One cycle: read the cursor → mark it running → fetch orders since the cursor → drop duplicates →
/// save the new orders and the new cursor in one transaction.
/// </summary>
/// <remarks>
/// A failure or a crash leaves the read position unchanged, so the next cycle fetches the same window
/// again; that is the retry. Skipped orders are not a failure. Talks to the outside only through
/// <see cref="IOrderSource"/>, <see cref="IIngestionStore"/> and <see cref="TimeProvider"/>.
/// </remarks>
public sealed class OrderIngestionService(
    IOrderSource source,
    IIngestionStore store,
    IngestionSettings settings,
    TimeProvider timeProvider)
{
    /// <summary>
    /// The read position never goes back before this date; orders themselves may be older. Also keeps
    /// "youngest − overlap" clear of <see cref="DateTimeOffset.MinValue"/>, where it would throw.
    /// </summary>
    private static readonly DateTimeOffset CursorFloor = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public async Task<IngestionResult> RunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();

        var cursor = await store.GetCursorAsync(cancellationToken);
        var since = ReadFrom(cursor.YoungestRecord);

        var unfinishedRunStartedAt = cursor.LastRunStatus == RunStatus.Running ? cursor.LastRun : null;

        await store.SaveCursorAsync(
            cursor with { LastRun = startedAt, LastRunStatus = RunStatus.Running },
            cancellationToken);

        try
        {
            var fetched = await source.FetchOrdersAsync(since, cancellationToken);

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
                unfinishedRunStartedAt,
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

    /// <summary>Best effort: if the database is what failed, this fails too, and the caller rethrows the original error.</summary>
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
        }
    }

    /// <summary>Newest stored order minus the overlap, never before <see cref="CursorFloor"/>. Null fetches everything.</summary>
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
