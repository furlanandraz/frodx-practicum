using Microsoft.Extensions.Time.Testing;
using OrderIngestion.Domain.Alerts;

namespace OrderIngestion.Tests.Alerts;

public sealed class AlertThrottleTests
{
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void First_alert_is_never_throttled()
    {
        var throttle = new AlertThrottle(_time, TimeSpan.FromMinutes(60));

        Assert.False(throttle.IsThrottled("api-not-found"));
    }

    [Fact]
    public void Same_alert_is_throttled_until_the_window_passes()
    {
        var throttle = new AlertThrottle(_time, TimeSpan.FromMinutes(60));
        throttle.MarkSent("api-not-found");

        _time.Advance(TimeSpan.FromMinutes(59));
        Assert.True(throttle.IsThrottled("api-not-found"));

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(throttle.IsThrottled("api-not-found"));
    }

    [Fact]
    public void Different_alerts_are_throttled_independently()
    {
        var throttle = new AlertThrottle(_time, TimeSpan.FromMinutes(60));
        throttle.MarkSent("api-not-found");

        Assert.False(throttle.IsThrottled("schema-drift:channel"));
    }

    [Fact]
    public void Unsent_alert_is_not_throttled()
    {
        // A failed send never calls MarkSent, so the next run tries to email again.
        var throttle = new AlertThrottle(_time, TimeSpan.FromMinutes(60));
        _ = throttle.IsThrottled("run-failed");

        Assert.False(throttle.IsThrottled("run-failed"));
    }

    [Fact]
    public void Zero_window_disables_throttling()
    {
        var throttle = new AlertThrottle(_time, TimeSpan.Zero);
        throttle.MarkSent("api-not-found");

        Assert.False(throttle.IsThrottled("api-not-found"));
    }
}
