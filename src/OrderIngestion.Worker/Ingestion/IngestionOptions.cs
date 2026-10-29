using System.ComponentModel.DataAnnotations;
using OrderIngestion.Domain.Ingestion;

namespace OrderIngestion.Worker.Ingestion;

/// <summary>Bound from the "Ingestion" config section. Checked at startup.</summary>
public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>Minutes between runs. The first run starts immediately on startup.</summary>
    [Range(1, 1440)]
    public int IntervalMinutes { get; init; }

    /// <summary>
    /// How far before the cursor each fetch starts again, to catch orders the upstream makes visible
    /// slightly late. 0 re-reads only the newest stored order(s); anything re-read is skipped by the hash.
    /// </summary>
    [Range(0, 10_080)]
    public int CursorOverlapMinutes { get; init; }

    public TimeSpan Interval => TimeSpan.FromMinutes(IntervalMinutes);

    public IngestionSettings ToSettings() => new(TimeSpan.FromMinutes(CursorOverlapMinutes));
}
