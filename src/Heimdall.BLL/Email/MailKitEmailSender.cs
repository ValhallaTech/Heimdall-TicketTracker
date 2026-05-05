using System;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Email;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Heimdall.BLL.Email;

/// <summary>
/// Production <see cref="IEmailSender"/> implementation built on
/// <a href="https://github.com/jstedfast/MailKit">MailKit</a> for SMTP transport
/// and <a href="https://github.com/jstedfast/MimeKit">MimeKit</a> for message
/// construction. Chosen over the deprecated <c>System.Net.Mail.SmtpClient</c>
/// for full RFC compliance, async / cancellation support, and active maintenance.
/// </summary>
public sealed class MailKitEmailSender : IEmailSender
{
    private readonly SmtpOptions _options;
    private readonly ILogger<MailKitEmailSender> _logger;

    /// <summary>Initializes a new instance of the <see cref="MailKitEmailSender"/> class.</summary>
    /// <param name="options">SMTP options bound from the <c>Email:Smtp</c> configuration section.</param>
    /// <param name="logger">Logger for delivery diagnostics. Bodies and credentials are never logged.</param>
    public MailKitEmailSender(IOptions<SmtpOptions> options, ILogger<MailKitEmailSender> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrWhiteSpace(message.To))
        {
            throw new ArgumentException("EmailMessage.To must be non-empty.", nameof(message));
        }

        if (string.IsNullOrWhiteSpace(message.Subject))
        {
            throw new ArgumentException("EmailMessage.Subject must be non-empty.", nameof(message));
        }

        if (message.HtmlBody is null && message.PlainTextBody is null)
        {
            throw new ArgumentException(
                "EmailMessage must supply at least one of HtmlBody or PlainTextBody.",
                nameof(message));
        }

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_options.FromDisplayName ?? "Heimdall", _options.From));
        mime.To.Add(MailboxAddress.Parse(message.To));

        if (message.Cc is not null)
        {
            foreach (var cc in message.Cc)
            {
                mime.Cc.Add(MailboxAddress.Parse(cc));
            }
        }

        if (message.Bcc is not null)
        {
            foreach (var bcc in message.Bcc)
            {
                mime.Bcc.Add(MailboxAddress.Parse(bcc));
            }
        }

        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder
        {
            HtmlBody = message.HtmlBody,
            TextBody = message.PlainTextBody,
        }.ToMessageBody();

        using var client = new SmtpClient
        {
            Timeout = _options.TimeoutMilliseconds,
        };

        try
        {
            var secureOptions = _options.UseStartTls
                ? SecureSocketOptions.StartTls
                : SecureSocketOptions.SslOnConnect;

            await client.ConnectAsync(_options.Host, _options.Port, secureOptions, cancellationToken)
                .ConfigureAwait(false);
            await client.AuthenticateAsync(_options.UserName, _options.Password, cancellationToken)
                .ConfigureAwait(false);
            await client.SendAsync(mime, cancellationToken).ConfigureAwait(false);

            // Subject is operator-controlled template text and is safe to log; the
            // recipient address is PII and is intentionally omitted.
            _logger.LogInformation("Sent email subject={Subject}", message.Subject);
        }
        catch (Exception ex)
        {
            // Do not log the recipient — message.To is PII. The exception (with SMTP
            // response code) and timing are sufficient for operational triage.
            _logger.LogError(ex, "Email delivery failed (subject={Subject})", message.Subject);
            throw;
        }
        finally
        {
            if (client.IsConnected)
            {
                try
                {
                    await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception disconnectEx) when (
                    disconnectEx is OperationCanceledException
                    or SmtpProtocolException
                    or SmtpCommandException
                    or System.IO.IOException
                    or ObjectDisposedException)
                {
                    // Disconnect failures must not mask the original delivery outcome.
                    _logger.LogWarning(disconnectEx, "SMTP disconnect failed after send.");
                }
            }
        }
    }
}
