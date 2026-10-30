namespace OrderIngestion.Domain.Ingestion;

public enum RunStatus
{
    /// <summary>
    /// Written when a run starts. Cycles in one process never overlap, so if the next run still
    /// finds this, the previous one never recorded a result: its process stopped (crash, kill,
    /// power loss), or the database couldn't be written at the end. Either way nothing it fetched
    /// was saved.
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
