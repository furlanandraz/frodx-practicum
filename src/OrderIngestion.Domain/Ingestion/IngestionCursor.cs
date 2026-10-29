namespace OrderIngestion.Domain.Ingestion;

public enum RunStatus
{
    /// <summary>
    /// Written when a run starts. If the next run still finds this, the previous one died
    /// mid-run (crash, kill, power loss) and nothing it fetched was saved.
    /// </summary>
    Running,
    Succeeded,
    Failed,
}

/// <summary>Where ingestion stands. Stored as the single row of the <c>cursor</c> table.</summary>
/// <param name="LastRun">When the latest run started. Also the timestamp of the batch that run wrote.</param>
/// <param name="YoungestRecord">CreatedAt of the newest order synced so far: the read position.</param>
public sealed record IngestionCursor(
    DateTimeOffset? LastRun,
    RunStatus? LastRunStatus,
    DateTimeOffset? YoungestRecord)
{
    /// <summary>Before the first run.</summary>
    public static IngestionCursor Empty { get; } = new(null, null, null);
}
