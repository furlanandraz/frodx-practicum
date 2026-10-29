using System.Data.Common;
using System.Globalization;
using Dapper;
using OrderIngestion.Domain.Ingestion;
using OrderIngestion.Domain.Orders;

namespace OrderIngestion.Infrastructure.Persistence;

/// <summary>
/// <see cref="IIngestionStore"/> on SQLite, with Dapper mapping parameters and rows.
/// All SQL is written out here; there is no ORM generating queries.
/// </summary>
internal sealed class SqliteIngestionStore(SqliteDatabase database) : IIngestionStore
{
    // ON CONFLICT DO NOTHING: an order already stored is skipped (0 rows affected),
    // which is how duplicates are counted.
    private const string InsertSyncSql = """
        INSERT INTO sync (canonical_hash, timestamp, payload)
        VALUES (@CanonicalHash, @Timestamp, @Payload)
        ON CONFLICT (canonical_hash) DO NOTHING;
        """;

    private const string SelectCursorSql = """
        SELECT last_run        AS LastRun,
               last_run_status AS LastRunStatus,
               youngest_record AS YoungestRecord
        FROM cursor
        WHERE id = 1;
        """;

    private const string UpsertCursorSql = """
        INSERT INTO cursor (id, last_run, last_run_status, youngest_record)
        VALUES (1, @LastRun, @LastRunStatus, @YoungestRecord)
        ON CONFLICT (id) DO UPDATE SET
            last_run        = excluded.last_run,
            last_run_status = excluded.last_run_status,
            youngest_record = excluded.youngest_record;
        """;

    public async Task<IngestionCursor> GetCursorAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<CursorRow>(
            new CommandDefinition(SelectCursorSql, cancellationToken: cancellationToken));

        return row?.ToCursor() ?? IngestionCursor.Empty;
    }

    public async Task<int> SaveBatchAsync(
        DateTimeOffset batchTimestamp,
        IReadOnlyList<Order> orders,
        IngestionCursor cursor,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var timestamp = Timestamp(batchTimestamp);

        // Dapper runs the statement once per item and adds up the affected rows.
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            InsertSyncSql,
            orders.Select(order => new { order.CanonicalHash, Timestamp = timestamp, order.Payload }),
            transaction,
            cancellationToken: cancellationToken));

        await UpsertCursorAsync(connection, transaction, cursor, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return inserted;
    }

    public async Task SaveCursorAsync(IngestionCursor cursor, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await UpsertCursorAsync(connection, transaction: null, cursor, cancellationToken);
    }

    private static Task UpsertCursorAsync(
        DbConnection connection, DbTransaction? transaction, IngestionCursor cursor, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(
            UpsertCursorSql,
            new
            {
                LastRun = Timestamp(cursor.LastRun),
                LastRunStatus = cursor.LastRunStatus?.ToString().ToLowerInvariant(),
                YoungestRecord = Timestamp(cursor.YoungestRecord),
            },
            transaction,
            cancellationToken: cancellationToken));

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? Timestamp(DateTimeOffset? value) => value is { } v ? Timestamp(v) : null;

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        value is null ? null : DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture);

    /// <summary>The cursor row as stored: plain strings, converted explicitly.</summary>
    private sealed class CursorRow
    {
        public string? LastRun { get; init; }
        public string? LastRunStatus { get; init; }
        public string? YoungestRecord { get; init; }

        public IngestionCursor ToCursor() => new(
            ParseTimestamp(LastRun),
            LastRunStatus is null ? null : Enum.Parse<RunStatus>(LastRunStatus, ignoreCase: true),
            ParseTimestamp(YoungestRecord));
    }
}
