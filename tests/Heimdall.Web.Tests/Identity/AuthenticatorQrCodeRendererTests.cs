using System;
using FluentAssertions;
using Heimdall.Web.Identity;

namespace Heimdall.Web.Tests.Identity;

/// <summary>
/// Unit tests for <see cref="AuthenticatorQrCodeRenderer"/> — verifies the
/// renderer produces a valid base64 PNG payload for any well-formed
/// <c>otpauth://</c> URI and refuses empty input.
/// </summary>
public class AuthenticatorQrCodeRendererTests
{
    private const string SampleOtpAuthUri =
        "otpauth://totp/Heimdall:user@example.com?secret=JBSWY3DPEHPK3PXP&issuer=Heimdall&digits=6";

    [Fact]
    public void Should_ReturnNonEmptyBase64String_When_GivenValidOtpAuthUri()
    {
        var renderer = new AuthenticatorQrCodeRenderer();

        var result = renderer.Render(SampleOtpAuthUri);

        result.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Should_ReturnDecodablePngBytes_When_GivenValidOtpAuthUri()
    {
        var renderer = new AuthenticatorQrCodeRenderer();

        var result = renderer.Render(SampleOtpAuthUri);
        byte[] bytes = Convert.FromBase64String(result);

        // PNG signature: 0x89 0x50 0x4E 0x47 0x0D 0x0A 0x1A 0x0A — assert the
        // first four bytes so we know the encoder actually produced a PNG.
        bytes.Should().HaveCountGreaterThan(8);
        bytes[0].Should().Be(0x89);
        bytes[1].Should().Be(0x50); // 'P'
        bytes[2].Should().Be(0x4E); // 'N'
        bytes[3].Should().Be(0x47); // 'G'
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Should_ThrowArgumentException_When_OtpAuthUriIsNullOrWhitespace(string? uri)
    {
        var renderer = new AuthenticatorQrCodeRenderer();

        Action act = () => renderer.Render(uri!);

        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("otpauthUri");
    }
}
