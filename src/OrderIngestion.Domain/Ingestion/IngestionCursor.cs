namespace OrderIngestion.Domain.Ingestion;

public enum RunStatus
{
    /// <summary>
    /// Written when a run starts. If the next run still finds it, the previous one never recorded a
    /// result (crash, kill, or the final write failed), and nothing it fetched was saved.
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
    public static IngestionCursor Empty { get; } = new(null, null, null);
}
