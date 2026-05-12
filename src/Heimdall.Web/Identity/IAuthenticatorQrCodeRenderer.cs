namespace Heimdall.Web.Identity;

/// <summary>
/// Renders an <c>otpauth://</c> URI into a base64-encoded PNG QR code suitable
/// for embedding in a server-rendered <c>&lt;img src="data:image/png;base64,…"&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// Introduced in Phase 4.3 step 9
/// (<c>docs/implementation/phase-4-checklist.md</c>). Kept behind an interface so
/// the MFA-setup page can be unit tested without dragging the QRCoder native-free
/// rendering pipeline into the test fixture, and so an alternative renderer
/// (e.g. SVG) can be swapped in without touching the page.
/// </para>
/// </remarks>
public interface IAuthenticatorQrCodeRenderer
{
    /// <summary>
    /// Renders the supplied <paramref name="otpauthUri"/> into a base64-encoded
    /// PNG payload. The caller is responsible for prefixing
    /// <c>data:image/png;base64,</c> when embedding into HTML.
    /// </summary>
    /// <param name="otpauthUri">
    /// The <c>otpauth://</c> URI produced by the MFA-setup page.
    /// </param>
    /// <returns>The base64-encoded PNG payload.</returns>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="otpauthUri"/> is <c>null</c>, empty, or whitespace.
    /// </exception>
    string Render(string otpauthUri);
}
