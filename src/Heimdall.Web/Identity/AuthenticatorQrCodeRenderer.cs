using System;
using QRCoder;

namespace Heimdall.Web.Identity;

/// <summary>
/// Default <see cref="IAuthenticatorQrCodeRenderer"/> implementation, backed by
/// QRCoder's pure-managed <see cref="PngByteQRCode"/> rasteriser. No native
/// dependencies — the renderer is happy on any platform Heimdall ships to
/// (Render's Linux runtime + Windows dev boxes alike).
/// </summary>
/// <remarks>
/// <para>
/// Phase 4.3 step 9 (<c>docs/implementation/phase-4-checklist.md</c>). Uses
/// error-correction level <c>Q</c> (~25% recovery) so the rendered QR is robust
/// to the lossy "phone camera over a laptop screen" enrolment path. Six pixels
/// per module keeps the encoded image compact (≈2 KB) while still scanning
/// reliably at typical browser zoom.
/// </para>
/// </remarks>
public sealed class AuthenticatorQrCodeRenderer : IAuthenticatorQrCodeRenderer
{
    private const int PixelsPerModule = 6;

    /// <inheritdoc />
    public string Render(string otpauthUri)
    {
        if (string.IsNullOrWhiteSpace(otpauthUri))
        {
            throw new ArgumentException(
                "otpauth URI must be a non-empty, non-whitespace string.",
                nameof(otpauthUri));
        }

        using QRCodeGenerator generator = new QRCodeGenerator();
        using QRCodeData data = generator.CreateQrCode(
            otpauthUri,
            QRCodeGenerator.ECCLevel.Q);
        using PngByteQRCode pngQrCode = new PngByteQRCode(data);

        byte[] png = pngQrCode.GetGraphic(PixelsPerModule);
        return Convert.ToBase64String(png);
    }
}
