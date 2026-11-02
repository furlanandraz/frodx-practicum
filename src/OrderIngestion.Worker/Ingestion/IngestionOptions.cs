using System.ComponentModel.DataAnnotations;
using OrderIngestion.Domain.Ingestion;

namespace OrderIngestion.Worker.Ingestion;

public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>Minutes between runs. The first run starts immediately on startup.</summary>
    [Range(1, 1440)]
    public int IntervalMinutes { get; init; }

    /// <summary>
    /// How far before the cursor each fetch starts, to catch orders the upstream shows late; re-reads
    /// are dropped by the hash. A placeholder: in production, the upstream's worst-case delay.
    /// </summary>
    [Range(0, 604_800)]
    public int CursorOverlapSeconds { get; init; }

    public TimeSpan Interval => TimeSpan.FromMinutes(IntervalMinutes);

    public IngestionSettings ToSettings() => new(TimeSpan.FromSeconds(CursorOverlapSeconds));
}
