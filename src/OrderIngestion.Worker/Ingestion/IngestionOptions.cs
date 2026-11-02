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
    /// Seconds before the cursor each fetch starts again, to catch orders the upstream makes visible
    /// slightly late. 0 re-reads only the newest stored order(s); anything re-read is skipped by the hash.
    /// A placeholder default: in production it follows the upstream's worst-case visibility delay.
    /// </summary>
    [Range(0, 604_800)]
    public int CursorOverlapSeconds { get; init; }

    public TimeSpan Interval => TimeSpan.FromMinutes(IntervalMinutes);

    public IngestionSettings ToSettings() => new(TimeSpan.FromSeconds(CursorOverlapSeconds));
}
