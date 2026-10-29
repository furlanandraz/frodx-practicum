namespace OrderIngestion.Domain.Alerts;

/// <summary>Something a person has to fix.</summary>
/// <param name="Key">Identifies "the same alert" for throttling, e.g. "api-not-found".</param>
public sealed record Alert(string Key, string Subject, string Body);

/// <summary>Delivers alerts to people (email today).</summary>
public interface IAlertNotifier
{
    Task SendAsync(Alert alert, CancellationToken cancellationToken);
}
