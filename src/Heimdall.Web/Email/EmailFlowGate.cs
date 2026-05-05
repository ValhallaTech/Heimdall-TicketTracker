using System;
using Heimdall.BLL.Email;
using Heimdall.Core.Email;

namespace Heimdall.Web.Email;

/// <summary>
/// Lightweight gate that surfaces whether email-driven self-service flows
/// (forgot-password, registration, email-confirmation) are currently usable
/// (Phase 1 step 10 of <c>docs/proposals/security-and-authorization.md</c> §9.3).
/// The active <see cref="IEmailSender"/> is decided at startup by
/// <c>AddHeimdallEmail</c>; when that registration falls back to
/// <c>NoOpEmailSender</c> (because SMTP isn't configured), email-driven flows
/// must surface a "currently unavailable" message rather than silently dropping
/// the mail on the floor.
/// </summary>
public sealed class EmailFlowGate
{
    private const string ActiveImplementation = "MailKitEmailSender";

    private const string NotActiveReason =
        "Email sender is the no-op fallback; configure SMTP to enable email-driven flows.";

    /// <summary>
    /// Initializes a new instance of the <see cref="EmailFlowGate"/> class
    /// against the supplied <paramref name="info"/> diagnostic marker registered
    /// by <c>AddHeimdallEmail</c>.
    /// </summary>
    /// <param name="info">The startup-decided email-sender registration info.</param>
    /// <exception cref="ArgumentNullException"><paramref name="info"/> is <c>null</c>.</exception>
    public EmailFlowGate(EmailSenderRegistrationInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        IsActive = string.Equals(
            info.ChosenImplementation,
            ActiveImplementation,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Gets a value indicating whether email-driven flows are currently active —
    /// i.e. the wired <see cref="IEmailSender"/> is <c>MailKitEmailSender</c>.
    /// </summary>
    public bool IsActive { get; }

    /// <summary>
    /// Gets a short, secret-free explanation suitable for surfacing on disabled-flow
    /// pages or in logs when <see cref="IsActive"/> is <c>false</c>. Returns an empty
    /// string when the gate is active.
    /// </summary>
    public string Reason => IsActive ? string.Empty : NotActiveReason;
}
