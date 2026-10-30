using Microsoft.Extensions.Options;
using OrderIngestion.Domain.Alerts;
using OrderIngestion.Domain.Ingestion;
using OrderIngestion.Domain.Orders;
using OrderIngestion.Worker.Alerts;

namespace OrderIngestion.Worker.Ingestion;

/// <summary>
/// The scheduler: one cycle on startup, then one every <see cref="IngestionOptions.IntervalMinutes"/>.
/// Replaces cron: cycles never overlap (the timer waits for the current one to finish), and Docker's
/// stop signal cancels the token so shutdown is clean. Every cycle logs its start and its result.
/// </summary>
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
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Ingestion worker stopping");
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        var runId = Guid.NewGuid();

        // Every log line written during this cycle carries RunId, so one cycle can be traced end to end.
        using var _ = logger.BeginScope(new Dictionary<string, object> { ["RunId"] = runId });
        logger.LogInformation("Ingestion run {RunId} started", runId);

        try
        {
            // A fresh DI scope per cycle: scoped services (the store) live for one cycle only.
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
            // An anticipated failure: one clear line, then an email if a person has to act.
            logger.LogError("Ingestion run {RunId} failed ({Kind:l}): {Reason}", runId, Describe(ex.Failure), ex.Message);
            await EmailIfNeededAsync(ex, runId, stoppingToken);
        }
        catch (Exception ex)
        {
            // Not anticipated: keep the stack trace. The worker survives and the next cycle tries again.
            logger.LogError(ex, "Ingestion run {RunId} failed unexpectedly (the next cycle tries again)", runId);
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
        if (result.InterruptedRunStartedAt is { } interruptedAt)
        {
            logger.LogWarning(
                "Previous run (started {InterruptedRunStartedAt:l}) was interrupted before it finished; nothing from it was saved, so this run fetched its window again",
                interruptedAt.ToString("O"));
        }

        // Skipped orders are logged, not emailed: a bad value in one order is a data problem, not an outage.
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
