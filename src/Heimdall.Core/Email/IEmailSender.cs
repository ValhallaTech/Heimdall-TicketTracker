using System.Threading;
using System.Threading.Tasks;

namespace Heimdall.Core.Email;

/// <summary>
/// Abstraction for sending transactional email. Two implementations are wired up
/// at startup based on configuration: a MailKit/MimeKit-backed production sender
/// and a no-op fallback for environments where SMTP has not been provisioned.
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Sends the supplied <paramref name="message"/>. Implementations MUST throw on a
    /// hard delivery failure (e.g. SMTP authentication, connection, or transport errors)
    /// so the caller can react, log, and audit. The no-op implementation always succeeds.
    /// </summary>
    /// <param name="message">The message to send. Must not be null.</param>
    /// <param name="cancellationToken">Token to observe while sending.</param>
    /// <returns>A task that completes when delivery has been attempted.</returns>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
