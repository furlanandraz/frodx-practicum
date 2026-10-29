namespace OrderIngestion.Domain.Ingestion;

/// <param name="CursorOverlap">
/// How far before the cursor to start reading again, to catch orders that show up late.
/// Re-read orders are dropped by the dedupe hash, so overlap costs nothing in correctness.
/// </param>
public sealed record IngestionSettings(TimeSpan CursorOverlap);
