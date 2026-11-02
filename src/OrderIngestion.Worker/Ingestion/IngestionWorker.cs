using System.Diagnostics;
using Microsoft.Extensions.Options;
using OrderIngestion.Domain.Alerts;
using OrderIngestion.Domain.Ingestion;
using OrderIngestion.Domain.Orders;
using OrderIngestion.Worker.Alerts;

namespace OrderIngestion.Worker.Ingestion;

/// <summary>
/// The scheduler: one cycle on startup, then one every <see cref="IngestionOptions.IntervalMinutes"/>.
/// Docker's stop signal cancels the token, so shutdown is clean.
/// </summary>
/// <remarks>
/// Cycles never overlap. One that outlasts the interval delays the next: the timer collapses the
/// ticks it missed into a single catch-up cycle.
/// </remarks>
internal sealed class IngestionWorker(
    IServiceScopeFactory scopeFactory,
    AlertDispatcher alerts,
    IOptions<IngestionOptions> options,
    ILogger<IngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.Interval;
        logger.LogInformation("Ingestion worker started; running now and then every {Interval}", interval);

        using var timer = new PeriodicTimer(interval);
        try
        {
            do
            {
                await RunOnceAsync(interval, stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Ingestion worker stopping");
        }
    }

    private async Task RunOnceAsync(TimeSpan interval, CancellationToken stoppingToken)
    {
        var runId = Guid.NewGuid();

        using var _ = logger.BeginScope(new Dictionary<string, object> { ["RunId"] = runId });
        logger.LogInformation("Ingestion run {RunId} started", runId);
        var started = Stopwatch.GetTimestamp();

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var ingestion = scope.ServiceProvider.GetRequiredService<OrderIngestionService>();

            LogResult(await ingestion.RunAsync(runId, stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OrderSourceException ex)
        {
            logger.LogError(
                "Ingestion run {RunId} failed ({Kind:l}) after {DurationMs:F0} ms: {Reason}",
                runId, Describe(ex.Failure), Stopwatch.GetElapsedTime(started).TotalMilliseconds, ex.Message);
            await EmailIfNeededAsync(ex, runId, stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex, "Ingestion run {RunId} failed unexpectedly after {DurationMs:F0} ms (the next cycle tries again)",
                runId, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        var duration = Stopwatch.GetElapsedTime(started);
        if (duration > interval)
        {
            logger.LogWarning(
                "Ingestion run {RunId} took {Duration:l}, longer than the {Interval:l} interval: the ticks it missed are skipped and the next cycle starts right away",
                runId, duration.ToString(@"hh\:mm\:ss"), interval.ToString(@"hh\:mm\:ss"));
        }
    }

    private async Task EmailIfNeededAsync(Exception exception, Guid runId, CancellationToken stoppingToken)
    {
        if (AlertRules.ForFailure(exception, runId) is { } alert)
        {
            await alerts.DispatchAsync(alert, stoppingToken);
        }
    }

    private static string Describe(OrderSourceFailure failure) => failure switch
    {
        OrderSourceFailure.NotFound => "404",
        OrderSourceFailure.Unauthorized => "401",
        OrderSourceFailure.SchemaDrift => "schema drift, nothing stored",
        _ => "the next cycle tries again",
    };

    private void LogResult(IngestionResult result)
    {
        if (result.UnfinishedRunStartedAt is { } unfinishedAt)
        {
            logger.LogWarning(
                "Previous run (started {UnfinishedRunStartedAt:l}) did not finish normally: its process stopped, or it could not record its result. Nothing from it was saved, so this run fetched its window again",
                unfinishedAt.ToString("O"));
        }

        foreach (var skipped in result.Skipped)
        {
            logger.LogWarning(
                "Skipped order #{Index} {ExternalId}: {Reason}", skipped.Index, skipped.ExternalId ?? "(no id)", skipped.Reason);
        }

        logger.LogInformation(
            "Ingestion run {RunId} completed: fetched {Fetched} since {Since:l}, inserted {Inserted}, duplicates {Duplicates}, skipped {Skipped} in {DurationMs:F0} ms",
            result.RunId,
            result.Fetched,
            result.Since?.ToString("O") ?? "the beginning",
            result.Inserted,
            result.Duplicates,
            result.Skipped.Count,
            result.Duration.TotalMilliseconds);
    }
}
