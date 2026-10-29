using Microsoft.Extensions.Time.Testing;
using OrderIngestion.Domain.Ingestion;
using OrderIngestion.Domain.Orders;
using OrderIngestion.Tests.TestSupport;

namespace OrderIngestion.Tests.Ingestion;

public sealed class OrderIngestionServiceTests
{
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(60);

    private readonly FakeTimeProvider _time = new(Sample.At("2026-10-29T12:00:00Z"));
    private readonly FakeSource _source = new();
    private readonly FakeStore _store = new();

    private OrderIngestionService Service => new(_source, _store, new IngestionSettings(Overlap), _time);

    [Fact]
    public async Task First_cycle_fetches_everything_and_stores_new_orders()
    {
        _source.Next = () => [Sample.Order("ORD-1"), Sample.Order("ORD-2")];

        var result = await Service.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(Assert.Single(_source.SinceRequests));
        Assert.Equal(2, result.Fetched);
        Assert.Equal(2, result.Inserted);
        Assert.Equal(0, result.Duplicates);
    }

    [Fact]
    public async Task Order_repeated_inside_one_response_is_a_duplicate()
    {
        _source.Next = () => [Sample.Order("ORD-1"), Sample.Order("ORD-1"), Sample.Order("ORD-2")];

        var result = await Service.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(2, result.Inserted);
        Assert.Equal(1, result.Duplicates);
    }

    [Fact]
    public async Task Next_cycle_reads_from_the_cursor_minus_the_overlap_and_skips_known_orders()
    {
        _source.Next = () =>
        [
            Sample.Order("ORD-1", createdAt: "2026-10-29T09:00:00Z"),
            Sample.Order("ORD-2", createdAt: "2026-10-29T11:00:00Z"),
        ];
        await Service.RunAsync(Guid.NewGuid(), CancellationToken.None);

        _source.Next = () => [Sample.Order("ORD-2", createdAt: "2026-10-29T11:00:00Z")];
        var second = await Service.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(Sample.At("2026-10-29T10:00:00Z"), _source.SinceRequests[1]); // youngest (11:00) − 60 min
        Assert.Equal(0, second.Inserted);
        Assert.Equal(1, second.Duplicates);
    }

    [Fact]
    public async Task Successful_cycle_moves_the_cursor_and_stamps_the_batch_with_its_start_time()
    {
        _source.Next = () => [Sample.Order("ORD-1", createdAt: "2026-10-29T11:30:00Z")];

        await Service.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(new IngestionCursor(_time.GetUtcNow(), RunStatus.Succeeded, Sample.At("2026-10-29T11:30:00Z")), _store.Cursor);
        Assert.Equal(_time.GetUtcNow(), _store.LastBatchTimestamp);
    }

    [Fact]
    public async Task Cursor_never_moves_backwards()
    {
        _store.Cursor = _store.Cursor with { YoungestRecord = Sample.At("2026-10-29T11:00:00Z") };
        _source.Next = () => [Sample.Order("ORD-OLD", createdAt: "2026-10-28T08:00:00Z")];

        await Service.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(Sample.At("2026-10-29T11:00:00Z"), _store.Cursor.YoungestRecord);
    }

    [Fact]
    public async Task Cycle_is_marked_running_before_fetching_without_moving_the_read_position()
    {
        var position = Sample.At("2026-10-29T11:00:00Z");
        _store.Cursor = _store.Cursor with { YoungestRecord = position };
        IngestionCursor? cursorDuringFetch = null;
        _source.Next = () =>
        {
            cursorDuringFetch = _store.Cursor;
            return [];
        };

        await Service.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(new IngestionCursor(_time.GetUtcNow(), RunStatus.Running, position), cursorDuringFetch);
    }

    [Fact]
    public async Task Cycle_left_running_by_a_crash_is_reported_and_its_window_fetched_again()
    {
        // What the database looks like after the process died mid-cycle.
        var crashedRunStartedAt = Sample.At("2026-10-29T11:45:00Z");
        _store.Cursor = new IngestionCursor(crashedRunStartedAt, RunStatus.Running, Sample.At("2026-10-29T11:00:00Z"));
        _source.Next = () => [Sample.Order("ORD-NEW", createdAt: "2026-10-29T11:30:00Z")];

        var result = await Service.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(crashedRunStartedAt, result.InterruptedRunStartedAt);
        Assert.Equal(Sample.At("2026-10-29T10:00:00Z"), Assert.Single(_source.SinceRequests)); // same window as before
        Assert.Equal(1, result.Inserted);
        Assert.Equal(RunStatus.Succeeded, _store.Cursor.LastRunStatus);
    }

    [Fact]
    public async Task Normal_previous_cycle_is_not_reported_as_interrupted()
    {
        _store.Cursor = new IngestionCursor(Sample.At("2026-10-29T11:45:00Z"), RunStatus.Succeeded, null);

        var result = await Service.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result.InterruptedRunStartedAt);
    }

    [Theory]
    [InlineData(OrderSourceFailure.NotFound)]
    [InlineData(OrderSourceFailure.Unauthorized)]
    [InlineData(OrderSourceFailure.SchemaDrift)]
    [InlineData(OrderSourceFailure.Other)]
    public async Task Failed_fetch_returns_early_storing_nothing_and_keeping_the_read_position(OrderSourceFailure failure)
    {
        var position = Sample.At("2026-10-29T11:00:00Z");
        _store.Cursor = _store.Cursor with { YoungestRecord = position };
        _source.Next = () => throw new OrderSourceException(failure, "details");

        await Assert.ThrowsAsync<OrderSourceException>(() => Service.RunAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(new IngestionCursor(_time.GetUtcNow(), RunStatus.Failed, position), _store.Cursor);
        Assert.Null(_store.LastBatchTimestamp); // no batch was saved
    }

    [Fact]
    public async Task Failed_save_marks_the_cycle_failed_and_keeps_the_read_position()
    {
        var position = Sample.At("2026-10-29T11:00:00Z");
        _store.Cursor = _store.Cursor with { YoungestRecord = position };
        _store.FailSave = true;
        _source.Next = () => [Sample.Order("ORD-NEW", createdAt: "2026-10-29T11:30:00Z")];

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.RunAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(position, _store.Cursor.YoungestRecord);
        Assert.Equal(RunStatus.Failed, _store.Cursor.LastRunStatus);
    }

    private sealed class FakeSource : IOrderSource
    {
        public Func<IReadOnlyList<Order>> Next { get; set; } = () => [];

        public List<DateTimeOffset?> SinceRequests { get; } = [];

        public Task<IReadOnlyList<Order>> FetchOrdersAsync(DateTimeOffset? since, CancellationToken cancellationToken)
        {
            SinceRequests.Add(since);
            return Task.FromResult(Next());
        }
    }

    /// <summary>In-memory store that behaves like the database: a set of hashes is the unique index.</summary>
    private sealed class FakeStore : IIngestionStore
    {
        private readonly HashSet<string> _hashes = [];

        public IngestionCursor Cursor { get; set; } = IngestionCursor.Empty;

        public DateTimeOffset? LastBatchTimestamp { get; private set; }

        /// <summary>Makes SaveBatchAsync fail like a database error would, saving nothing.</summary>
        public bool FailSave { get; set; }

        public Task<IngestionCursor> GetCursorAsync(CancellationToken cancellationToken) => Task.FromResult(Cursor);

        public Task<int> SaveBatchAsync(
            DateTimeOffset batchTimestamp,
            IReadOnlyList<Order> orders,
            IngestionCursor cursor,
            CancellationToken cancellationToken)
        {
            if (FailSave)
            {
                throw new InvalidOperationException("database is locked");
            }

            var inserted = orders.Count(order => _hashes.Add(order.CanonicalHash));
            LastBatchTimestamp = batchTimestamp;
            Cursor = cursor;
            return Task.FromResult(inserted);
        }

        public Task SaveCursorAsync(IngestionCursor cursor, CancellationToken cancellationToken)
        {
            Cursor = cursor;
            return Task.CompletedTask;
        }
    }
}
