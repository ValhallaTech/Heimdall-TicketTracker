using System;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Email;
using Microsoft.Extensions.Logging;

namespace Heimdall.BLL.Email;

/// <summary>
/// Development / SMTP-not-yet-provisioned fallback <see cref="IEmailSender"/>
/// implementation. Logs the suppressed delivery and returns successfully —
/// it is wired up automatically by <c>AddHeimdallEmail</c> when any of the
/// required <c>Email:Smtp</c> keys (Host, UserName, Password, From) are empty.
/// </summary>
public sealed class NoOpEmailSender : IEmailSender
{
    private readonly ILogger<NoOpEmailSender> _logger;

    /// <summary>Initializes a new instance of the <see cref="NoOpEmailSender"/> class.</summary>
    /// <param name="logger">Logger used to record suppressed deliveries.</param>
    public NoOpEmailSender(ILogger<NoOpEmailSender> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        _logger.LogInformation(
            "NoOpEmailSender suppressed email to {Recipient} subject={Subject} (configure SMTP to send real emails)",
            message.To,
            message.Subject);

        return Task.CompletedTask;
    }
}
