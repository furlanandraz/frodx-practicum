using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using OrderIngestion.Domain.Alerts;
using SmtpClient = MailKit.Net.Smtp.SmtpClient;

namespace OrderIngestion.Infrastructure.Alerts;

/// <summary>
/// Sends alerts as plain-text email with MailKit, which Microsoft recommends over the built-in
/// System.Net.Mail.SmtpClient. Throws on failure; the caller decides what a failed alert means.
/// </summary>
internal sealed class SmtpAlertNotifier(IOptions<AlertOptions> options) : IAlertNotifier
{
    public async Task SendAsync(Alert alert, CancellationToken cancellationToken)
    {
        var settings = options.Value;

        using var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(settings.From));
        foreach (var recipient in settings.GetRecipients())
        {
            message.To.Add(MailboxAddress.Parse(recipient));
        }

        message.Subject = $"[order-ingestion] {alert.Subject}";
        message.Body = new TextPart("plain") { Text = alert.Body };

        using var client = new SmtpClient { Timeout = 10_000 };

        await client.ConnectAsync(settings.SmtpHost, settings.SmtpPort, SecureSocketOptions.Auto, cancellationToken);
        if (!string.IsNullOrEmpty(settings.SmtpUsername))
        {
            await client.AuthenticateAsync(settings.SmtpUsername, settings.SmtpPassword ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }
}
