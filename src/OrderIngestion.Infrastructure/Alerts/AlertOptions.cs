using System.ComponentModel.DataAnnotations;
using MimeKit;

namespace OrderIngestion.Infrastructure.Alerts;

/// <summary>Bound from the "Alerts" config section.</summary>
public sealed class AlertOptions
{
    public const string SectionName = "Alerts";

    /// <summary>When false, alerts are still logged but not emailed.</summary>
    public bool Enabled { get; init; }

    public string SmtpHost { get; init; } = string.Empty;

    [Range(1, 65535)]
    public int SmtpPort { get; init; }

    /// <summary>Leave empty for servers without authentication (Mailpit).</summary>
    public string? SmtpUsername { get; init; }

    public string? SmtpPassword { get; init; }

    public string From { get; init; } = string.Empty;

    /// <summary>One or more addresses, comma-separated.</summary>
    public string To { get; init; } = string.Empty;

    /// <summary>The same alert is emailed at most once per this many minutes. 0 = no throttling.</summary>
    [Range(0, 1440)]
    public int ThrottleMinutes { get; init; }

    public IReadOnlyList<string> GetRecipients() =>
        To.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Extra startup check: with alerts on, a mail server and real addresses are needed.</summary>
    public bool IsCompleteWhenEnabled() =>
        !Enabled
        || (!string.IsNullOrWhiteSpace(SmtpHost)
            && MailboxAddress.TryParse(From, out _)
            && GetRecipients() is { Count: > 0 } recipients
            && recipients.All(address => MailboxAddress.TryParse(address, out _)));
}
