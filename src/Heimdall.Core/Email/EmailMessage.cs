using System.Collections.Generic;

namespace Heimdall.Core.Email;

/// <summary>
/// Transport-agnostic email message DTO consumed by <see cref="IEmailSender"/>.
/// Deliberately MimeKit-free so <c>Heimdall.Core</c> does not take a transitive
/// dependency on the SMTP transport library.
/// </summary>
/// <remarks>
/// This DTO is part of the public seam between application code and email
/// delivery — keep it stable. New optional fields should be additive.
/// </remarks>
public sealed class EmailMessage
{
    /// <summary>Gets or sets the primary recipient's email address. Required and non-empty.</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>Gets or sets the message subject. Required and non-empty.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the HTML body. May be <see langword="null"/> when only a plain-text
    /// body is supplied. At least one of <see cref="HtmlBody"/> or <see cref="PlainTextBody"/>
    /// must be set when the message is sent.
    /// </summary>
    public string? HtmlBody { get; set; }

    /// <summary>
    /// Gets or sets the plain-text body. May be <see langword="null"/>; when null and
    /// <see cref="HtmlBody"/> is set, the MailKit-based sender derives a plain-text
    /// alternative automatically.
    /// </summary>
    public string? PlainTextBody { get; set; }

    /// <summary>Gets or sets the optional list of carbon-copy recipients. <see langword="null"/> means none.</summary>
    public IReadOnlyList<string>? Cc { get; set; }

    /// <summary>Gets or sets the optional list of blind-carbon-copy recipients. <see langword="null"/> means none.</summary>
    public IReadOnlyList<string>? Bcc { get; set; }
}
