namespace Heimdall.BLL.Email;

/// <summary>
/// Strongly-typed options bound to the <c>Email:Smtp</c> configuration section.
/// </summary>
/// <remarks>
/// Validation happens at the DI registration site — see
/// <c>EmailServiceCollectionExtensions.AddHeimdallEmail</c> — rather than via
/// data annotations, because the registration logic decides which
/// <see cref="Heimdall.Core.Email.IEmailSender"/> implementation to wire up
/// based on which keys are present.
/// </remarks>
public sealed class SmtpOptions
{
    /// <summary>Gets or sets the SMTP server host (e.g. <c>smtp.example.com</c>). Empty when not configured.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Gets or sets the SMTP server port. Defaults to <c>587</c> (STARTTLS submission).</summary>
    public int Port { get; set; } = 587;

    /// <summary>Gets or sets the SMTP user name. Empty when not configured.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the SMTP password. Treated as a secret — never logged, echoed,
    /// or surfaced via the registration-info marker; only ever read inside
    /// <c>MailKitEmailSender</c> at the moment of authentication.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Gets or sets the sender address used for the <c>From</c> header. Empty when not configured.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional display name to pair with <see cref="From"/>.
    /// When <see langword="null"/>, the sender uses the literal string <c>"Heimdall"</c>.
    /// </summary>
    public string? FromDisplayName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether STARTTLS is used on connect.
    /// Defaults to <see langword="true"/> (the correct setting for submission port 587).
    /// When <see langword="false"/>, the sender connects with implicit TLS (port-465 style).
    /// </summary>
    public bool UseStartTls { get; set; } = true;

    /// <summary>Gets or sets the SMTP client timeout in milliseconds. Defaults to 30 seconds.</summary>
    public int TimeoutMilliseconds { get; set; } = 30_000;
}
