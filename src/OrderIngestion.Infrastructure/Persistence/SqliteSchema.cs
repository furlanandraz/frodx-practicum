namespace OrderIngestion.Infrastructure.Persistence;

/// <summary>
/// The whole database schema. Applied on every startup; IF NOT EXISTS makes that safe.
/// Plain SQL on purpose: what you read here is exactly what exists in the file.
/// </summary>
/// <remarks>
/// SQLite has no real TIMESTAMP or JSON column types; it accepts the names and stores the values
/// as text. Timestamps are ISO-8601 UTC ("2026-10-29T12:00:00.0000000+00:00"), which sorts
/// correctly as text. The JSON column is checked with json_valid().
/// </remarks>
internal static class SqliteSchema
{
    public const string Sql = """
        -- One row per synced order. All rows written by the same run share its timestamp.
        CREATE TABLE IF NOT EXISTS sync (
            id              INTEGER   PRIMARY KEY,
            canonical_hash  CHAR(64)  NOT NULL,   -- sha256(order id + customer id), hex: the dedupe key
            timestamp       TIMESTAMP NOT NULL,   -- the batch: when the run that synced this row started
            -- The order exactly as received. Can be omitted (left NULL, or the column dropped) for GDPR
            -- compliance when the app only needs to sync and must not store personal data.
            payload         JSON      CHECK (payload IS NULL OR json_valid(payload))
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_sync_canonical_hash ON sync (canonical_hash);

        -- Where ingestion stands. Exactly one row (id = 1).
        CREATE TABLE IF NOT EXISTS cursor (
            id               INTEGER   PRIMARY KEY CHECK (id = 1),
            last_run         TIMESTAMP NOT NULL,   -- when the latest run started
            last_run_status  TEXT      NOT NULL CHECK (last_run_status IN ('running', 'succeeded', 'failed')),
            youngest_record  TIMESTAMP             -- createdAt of the newest synced order; NULL until the first
        );
        """;
}
