using Microsoft.Extensions.Options;
using OrderIngestion.Domain.Alerts;
using OrderIngestion.Infrastructure.Alerts;

namespace OrderIngestion.Worker.Alerts;

/// <summary>
/// The one place alerts leave the process: applies the on/off switch and throttling, and makes
/// sure a failing mail server can never take the worker down.
/// </summary>
internal sealed class AlertDispatcher(
    IAlertNotifier notifier,
    AlertThrottle throttle,
    IOptions<AlertOptions> options,
    ILogger<AlertDispatcher> logger)
{
    public async Task DispatchAsync(Alert alert, CancellationToken cancellationToken)
    {
        var settings = options.Value;

        if (!settings.Enabled)
        {
            logger.LogInformation("Alert {AlertKey} raised; email alerts are disabled", alert.Key);
            return;
        }

        if (throttle.IsThrottled(alert.Key))
        {
            logger.LogInformation(
                "Alert {AlertKey} raised; already emailed within the last {ThrottleMinutes} min", alert.Key, settings.ThrottleMinutes);
            return;
        }

        try
        {
            await notifier.SendAsync(alert, cancellationToken);
            throttle.MarkSent(alert.Key);
            logger.LogInformation("Alert {AlertKey} emailed: {AlertSubject}", alert.Key, alert.Subject);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The underlying problem is already logged; this only records that the email didn't go out.
            logger.LogError(ex, "Could not email alert {AlertKey}", alert.Key);
        }
    }
}
