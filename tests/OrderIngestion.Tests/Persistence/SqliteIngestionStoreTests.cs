using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using OrderIngestion.Domain.Ingestion;
using OrderIngestion.Domain.Orders;
using OrderIngestion.Infrastructure.Persistence;
using OrderIngestion.Tests.TestSupport;

namespace OrderIngestion.Tests.Persistence;

/// <summary>Against a real SQLite file, so the SQL itself (unique index, ON CONFLICT, CHECKs) is tested.</summary>
public sealed class SqliteIngestionStoreTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Batch = Sample.At("2026-10-29T12:00:00Z");
    private static readonly IngestionCursor CursorAfterBatch = new(Batch, RunStatus.Succeeded, Sample.At("2026-10-29T10:00:00Z"));

    private readonly string _filePath = Path.Combine(Path.GetTempPath(), $"order-ingestion-test-{Guid.NewGuid():N}.db");
    private SqliteDatabase _database = null!;
    private SqliteIngestionStore _store = null!;

    public async Task InitializeAsync()
    {
        _database = new SqliteDatabase(Options.Create(new DatabaseOptions { FilePath = _filePath }));
        await _database.InitializeAsync(CancellationToken.None);
        _store = new SqliteIngestionStore(_database);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools(); // release the file so it can be deleted
        foreach (var file in new[] { _filePath, $"{_filePath}-wal", $"{_filePath}-shm" })
        {
            File.Delete(file);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Saving_the_same_orders_twice_stores_them_once()
    {
        Order[] orders = [Sample.Order("ORD-1"), Sample.Order("ORD-2")];

        var first = await Save(orders);
        var second = await Save(orders);

        Assert.Equal(2, first);
        Assert.Equal(0, second);
        Assert.Equal(2, await Count("sync"));
    }

    [Fact]
    public async Task Same_order_id_for_another_customer_is_stored_separately()
    {
        var inserted = await Save([Sample.Order("ORD-1", customerId: "CUST-1"), Sample.Order("ORD-1", customerId: "CUST-2")]);

        Assert.Equal(2, inserted);
    }

    [Fact]
    public async Task Rows_carry_the_hash_the_batch_timestamp_and_the_payload()
    {
        var order = Sample.Order("ORD-1");

        await Save([order]);

        var row = await QuerySingle<(string Hash, string Timestamp, string Payload)>(
            "SELECT canonical_hash, timestamp, payload FROM sync");
        Assert.Equal(order.CanonicalHash, row.Hash);
        Assert.Equal("2026-10-29T12:00:00.0000000+00:00", row.Timestamp);
        Assert.Equal(order.Payload, row.Payload);
    }

    [Fact]
    public async Task Cursor_is_empty_before_the_first_run()
    {
        Assert.Equal(IngestionCursor.Empty, await _store.GetCursorAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Cursor_round_trips_exactly_and_stays_a_single_row()
    {
        var running = new IngestionCursor(Sample.At("2026-10-29T12:00:00.1234567Z"), RunStatus.Running, null);
        var done = running with { LastRunStatus = RunStatus.Succeeded, YoungestRecord = Sample.At("2026-10-29T07:05:33Z") };

        await _store.SaveCursorAsync(running, CancellationToken.None);
        Assert.Equal(running, await _store.GetCursorAsync(CancellationToken.None));

        await _store.SaveCursorAsync(done, CancellationToken.None);
        Assert.Equal(done, await _store.GetCursorAsync(CancellationToken.None));
        Assert.Equal(1, await Count("cursor"));
    }

    [Fact]
    public async Task Orders_and_cursor_are_saved_together()
    {
        await Save([Sample.Order("ORD-1")]);

        Assert.Equal(CursorAfterBatch, await _store.GetCursorAsync(CancellationToken.None));
        Assert.Equal(1, await Count("sync"));
    }

    [Fact]
    public async Task Failure_halfway_through_a_save_leaves_nothing_behind()
    {
        // The second order breaks the json_valid() check after the first one was already inserted
        // inside the transaction: the same situation as the process dying mid-save.
        Order[] orders = [Sample.Order("ORD-1"), Sample.Order("ORD-2") with { Payload = "not json" }];

        await Assert.ThrowsAsync<SqliteException>(
            () => _store.SaveBatchAsync(Batch, orders, CursorAfterBatch, CancellationToken.None));

        Assert.Equal(0, await Count("sync"));
        Assert.Equal(IngestionCursor.Empty, await _store.GetCursorAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Database_refuses_a_second_cursor_row()
    {
        await _store.SaveCursorAsync(CursorAfterBatch, CancellationToken.None);

        await using var connection = await _database.OpenAsync(CancellationToken.None);
        await Assert.ThrowsAsync<SqliteException>(() => connection.ExecuteAsync(
            "INSERT INTO cursor (id, last_run, last_run_status) VALUES (2, '2026-10-29', 'succeeded')"));
    }

    private Task<int> Save(IReadOnlyList<Order> orders) =>
        _store.SaveBatchAsync(Batch, orders, CursorAfterBatch, CancellationToken.None);

    private async Task<long> Count(string table)
    {
        await using var connection = await _database.OpenAsync(CancellationToken.None);
        return await connection.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table}");
    }

    private async Task<T> QuerySingle<T>(string sql)
    {
        await using var connection = await _database.OpenAsync(CancellationToken.None);
        return await connection.QuerySingleAsync<T>(sql);
    }
}
