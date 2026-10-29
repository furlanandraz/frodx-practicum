namespace OrderIngestion.Domain.Alerts;

/// <summary>
/// Stops the same alert from being emailed every 15 minutes while a problem persists.
/// In memory: a restart forgets it, so the first alert after a restart always goes out.
/// </summary>
public sealed class AlertThrottle(TimeProvider timeProvider, TimeSpan window)
{
    private readonly Dictionary<string, DateTimeOffset> _lastSent = [];
    private readonly Lock _gate = new();

    public bool IsThrottled(string key)
    {
        lock (_gate)
        {
            return _lastSent.TryGetValue(key, out var lastSent)
                && timeProvider.GetUtcNow() - lastSent < window;
        }
    }

    /// <summary>Call only after the alert was actually delivered, so a failed send is retried next time.</summary>
    public void MarkSent(string key)
    {
        lock (_gate)
        {
            _lastSent[key] = timeProvider.GetUtcNow();
        }
    }
}
