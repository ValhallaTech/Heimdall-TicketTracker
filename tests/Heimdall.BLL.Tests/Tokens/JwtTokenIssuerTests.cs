using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Heimdall.BLL.Tokens;
using Heimdall.Core.Models;
using Heimdall.Core.Tokens;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Moq;
using Xunit;

namespace Heimdall.BLL.Tests.Tokens;

/// <summary>
/// Phase 5.3 step 7 — unit tests for <see cref="JwtTokenIssuer"/>. Covers the
/// happy paths for both permitted algorithms (RS256, ES256), claim-shape pinning,
/// the no-key-caching invariant (two consecutive issuance calls must see two
/// reference-distinct <see cref="SigningCredentialsResult"/> instances), and the
/// defensive guards that surface a misconfigured <c>signing_keys</c> row as an
/// <see cref="InvalidOperationException"/> at issue time rather than as an opaque
/// verifier failure downstream.
/// </summary>
public sealed class JwtTokenIssuerTests
{
    private const string TestIssuer = "https://heimdall.local";
    private const string TestAudience = "heimdall.api";
    private static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(15);

    private static TokenOptions BuildOptions() => new()
    {
        AccessTokenLifetime  = AccessLifetime,
        SigningKeyOverlap    = AccessLifetime,
        SigningKeyValidity   = TimeSpan.FromDays(90),
        RefreshTokenLifetime = TimeSpan.FromDays(14),
        Issuer   = TestIssuer,
        Audience = TestAudience,
    };

    private static HeimdallUser SampleUser(bool mfaEnabled = false) => new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Email = "tester@example.com",
        NormalizedEmail = "TESTER@EXAMPLE.COM",
        SecurityStamp = "stamp",
        ConcurrencyStamp = "concur",
        TwoFactorEnabled = mfaEnabled,
    };

    private static SigningCredentialsResult CreateRsaCredentials(string kid)
        => new(kid, SigningAlgorithm.Rs256, RSA.Create(2048));

    private static SigningCredentialsResult CreateEcCredentials(string kid)
        => new(kid, SigningAlgorithm.Es256, ECDsa.Create(ECCurve.NamedCurves.nistP256));

    private static JwtTokenIssuer CreateSut(
        ISigningKeyService signingKeys,
        TokenOptions? options = null,
        TimeProvider? timeProvider = null)
        => new(
            signingKeys,
            Options.Create(options ?? BuildOptions()),
            NullLogger<JwtTokenIssuer>.Instance,
            timeProvider);

    // -----------------------------------------------------------------------------
    // Happy paths — RS256 and ES256
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task Should_IssueRs256Token_When_CurrentKeyIsRsa()
    {
        // Arrange
        const string kid = "kid-rsa-1";
        var keys = new Mock<ISigningKeyService>(MockBehavior.Strict);
        keys.Setup(k => k.GetCurrentSigningCredentialsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => CreateRsaCredentials(kid));

        TokenOptions options = BuildOptions();
        var time = new TestTimeProvider(DateTimeOffset.Parse(
            "2030-01-15T12:00:00Z", CultureInfo.InvariantCulture));
        var sut = CreateSut(keys.Object, options, time);
        HeimdallUser user = SampleUser(mfaEnabled: false);

        // Act
        IssuedAccessToken issued = await sut.IssueAccessTokenAsync(user, new[] { "pwd" });

        // Assert — envelope.
        issued.Should().NotBeNull();
        issued.Jwt.Should().NotBeNullOrWhiteSpace();
        issued.ExpiresAt.Should().Be(issued.IssuedAt + AccessLifetime);
        issued.IssuedAt.Should().Be(time.GetUtcNow());

        // Header pinning — alg + kid + typ.
        var handler = new JsonWebTokenHandler();
        JsonWebToken parsed = handler.ReadJsonWebToken(issued.Jwt);
        parsed.Alg.Should().Be("RS256");
        parsed.Kid.Should().Be(kid);

        // Claim-shape pinning.
        parsed.GetClaim("sub").Value.Should().Be(user.Id.ToString());
        parsed.GetClaim("email").Value.Should().Be(user.Email);
        parsed.GetClaim("jti").Value.Should().Be(issued.Jti);
        parsed.GetClaim("mfa_enrolled").Value.Should().Be("false");
        parsed.Issuer.Should().Be(TestIssuer);
        parsed.Audiences.Should().ContainSingle().Which.Should().Be(TestAudience);

        // iat / nbf / exp — JsonWebToken parses these as DateTime (UTC).
        parsed.IssuedAt.Should().BeCloseTo(time.GetUtcNow().UtcDateTime, TimeSpan.FromSeconds(1));
        parsed.ValidFrom.Should().BeCloseTo(time.GetUtcNow().UtcDateTime, TimeSpan.FromSeconds(1));
        parsed.ValidTo.Should().BeCloseTo((time.GetUtcNow() + AccessLifetime).UtcDateTime, TimeSpan.FromSeconds(1));
        (parsed.ValidTo - parsed.IssuedAt).Should().Be(AccessLifetime);
    }

    [Fact]
    public async Task Should_IssueEs256Token_When_CurrentKeyIsEcdsa()
    {
        // Arrange
        const string kid = "kid-ec-1";
        var keys = new Mock<ISigningKeyService>(MockBehavior.Strict);
        keys.Setup(k => k.GetCurrentSigningCredentialsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => CreateEcCredentials(kid));

        var sut = CreateSut(keys.Object);

        // Act
        IssuedAccessToken issued = await sut.IssueAccessTokenAsync(SampleUser(), new[] { "pwd" });

        // Assert
        var handler = new JsonWebTokenHandler();
        JsonWebToken parsed = handler.ReadJsonWebToken(issued.Jwt);
        parsed.Alg.Should().Be("ES256");
        parsed.Kid.Should().Be(kid);
    }

    [Fact]
    public async Task Should_PropagateAmrAsMultiValuedClaim_When_CallerPassesMultipleEntries()
    {
        // Arrange
        var keys = new Mock<ISigningKeyService>(MockBehavior.Strict);
        keys.Setup(k => k.GetCurrentSigningCredentialsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => CreateRsaCredentials("kid-amr"));

        var sut = CreateSut(keys.Object);

        // Act
        IssuedAccessToken issued = await sut.IssueAccessTokenAsync(
            SampleUser(), new[] { "pwd", "mfa" });

        // Assert — amr round-trips as an array of two strings.
        var handler = new JsonWebTokenHandler();
        JsonWebToken parsed = handler.ReadJsonWebToken(issued.Jwt);
        IEnumerable<string> amrValues = parsed.GetClaim("amr").Value.StartsWith("[", StringComparison.Ordinal)
            ? System.Text.Json.JsonSerializer.Deserialize<List<string>>(parsed.GetClaim("amr").Value)!
            : parsed.Claims.Where(c => c.Type == "amr").Select(c => c.Value);

        amrValues.Should().BeEquivalentTo(new[] { "pwd", "mfa" });
    }

    [Fact]
    public async Task Should_SerialiseMfaEnrolledAsTrue_When_UserHasTwoFactorEnabled()
    {
        // Arrange
        var keys = new Mock<ISigningKeyService>(MockBehavior.Strict);
        keys.Setup(k => k.GetCurrentSigningCredentialsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => CreateRsaCredentials("kid-mfa"));

        var sut = CreateSut(keys.Object);

        // Act
        IssuedAccessToken issued = await sut.IssueAccessTokenAsync(
            SampleUser(mfaEnabled: true), new[] { "pwd", "mfa" });

        // Assert
        var handler = new JsonWebTokenHandler();
        JsonWebToken parsed = handler.ReadJsonWebToken(issued.Jwt);
        parsed.GetClaim("mfa_enrolled").Value.Should().Be("true");
    }

    // -----------------------------------------------------------------------------
    // Argument validation
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task Should_Throw_When_UserIsNull()
    {
        var keys = new Mock<ISigningKeyService>(MockBehavior.Strict);
        var sut = CreateSut(keys.Object);

        Func<Task> act = () => sut.IssueAccessTokenAsync(user: null!, amr: new[] { "pwd" });

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_Throw_When_AmrIsNull()
    {
        var keys = new Mock<ISigningKeyService>(MockBehavior.Strict);
        var sut = CreateSut(keys.Object);

        Func<Task> act = () => sut.IssueAccessTokenAsync(SampleUser(), amr: null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_Throw_When_AmrIsEmpty()
    {
        var keys = new Mock<ISigningKeyService>(MockBehavior.Strict);
        var sut = CreateSut(keys.Object);

        Func<Task> act = () => sut.IssueAccessTokenAsync(SampleUser(), amr: Array.Empty<string>());

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Should_Throw_When_SigningKeyServiceIsNull()
    {
        Action act = () => new JwtTokenIssuer(
            signingKeys: null!,
            Options.Create(BuildOptions()),
            NullLogger<JwtTokenIssuer>.Instance);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_OptionsIsNull()
    {
        var keys = new Mock<ISigningKeyService>(MockBehavior.Strict);

        Action act = () => new JwtTokenIssuer(
            keys.Object,
            options: null!,
            NullLogger<JwtTokenIssuer>.Instance);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_LoggerIsNull()
    {
        var keys = new Mock<ISigningKeyService>(MockBehavior.Strict);

        Action act = () => new JwtTokenIssuer(
            keys.Object,
            Options.Create(BuildOptions()),
            logger: null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // -----------------------------------------------------------------------------
    // Failure paths — no signing key, misconfigured alg
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task Should_SurfaceInvalidOperationException_When_NoCurrentSigningKey()
    {
        // Arrange — SigningKeyService throws InvalidOperationException when there
        // is no active signing key; the issuer must propagate it.
        var keys = new Mock<ISigningKeyService>(MockBehavior.Strict);
        keys.Setup(k => k.GetCurrentSigningCredentialsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no current signing key"));

        var sut = CreateSut(keys.Object);

        // Act
        Func<Task> act = () => sut.IssueAccessTokenAsync(SampleUser(), new[] { "pwd" });

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("no current signing key");
    }

    [Fact]
    public async Task Should_Throw_When_SigningCredentialsAlgIsOutsidePermittedSet()
    {
        // Arrange — fabricate a SigningCredentialsResult with an out-of-band Alg
        // (cast outside the enum) to pin the defensive guard at the issuer level.
        // This guard means a misconfigured signing_keys row surfaces as a clear
        // InvalidOperationException at issue time rather than an opaque downstream
        // verifier failure.
        var keys = new Mock<ISigningKeyService>(MockBehavior.Strict);
        keys.Setup(k => k.GetCurrentSigningCredentialsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SigningCredentialsResult(
                "kid-bad",
                (SigningAlgorithm)999,
                RSA.Create(2048)));

        var sut = CreateSut(keys.Object);

        // Act
        Func<Task> act = () => sut.IssueAccessTokenAsync(SampleUser(), new[] { "pwd" });

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("999");
    }

    // -----------------------------------------------------------------------------
    // No caching across calls (hardening §2.1)
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task Should_NotCacheSigningCredentials_When_IssuingTwiceInARow()
    {
        // Arrange — SetupSequence returns a fresh SigningCredentialsResult per call.
        // Capture references so we can assert reference-distinctness afterwards.
        SigningCredentialsResult first = CreateRsaCredentials("kid-a");
        SigningCredentialsResult second = CreateRsaCredentials("kid-b");

        var keys = new Mock<ISigningKeyService>(MockBehavior.Strict);
        keys.SetupSequence(k => k.GetCurrentSigningCredentialsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(first)
            .ReturnsAsync(second);

        var sut = CreateSut(keys.Object);
        HeimdallUser user = SampleUser();

        // Act
        IssuedAccessToken issuedA = await sut.IssueAccessTokenAsync(user, new[] { "pwd" });
        IssuedAccessToken issuedB = await sut.IssueAccessTokenAsync(user, new[] { "pwd" });

        // Assert — distinct jtis and the mock was invoked twice (no caching).
        issuedA.Jti.Should().NotBe(issuedB.Jti);
        keys.Verify(
            k => k.GetCurrentSigningCredentialsAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        // The two SigningCredentialsResult instances were reference-distinct.
        ReferenceEquals(first, second).Should().BeFalse();

        // Both kids must round-trip into their respective JWT headers.
        var handler = new JsonWebTokenHandler();
        handler.ReadJsonWebToken(issuedA.Jwt).Kid.Should().Be("kid-a");
        handler.ReadJsonWebToken(issuedB.Jwt).Kid.Should().Be("kid-b");
    }

    // -----------------------------------------------------------------------------
    // Refresh-token material round-trip
    // -----------------------------------------------------------------------------

    [Fact]
    public void Should_HashPlaintextWithRefreshTokenHasher_When_GeneratingRefreshTokenMaterial()
    {
        // Arrange
        var keys = new Mock<ISigningKeyService>(MockBehavior.Strict);
        var sut = CreateSut(keys.Object);

        // Act
        (string plaintext, string hash) = sut.GenerateRefreshTokenMaterial();

        // Assert — round-trip property: hash == RefreshTokenHasher.ComputeHash(plaintext).
        plaintext.Should().NotBeNullOrWhiteSpace();
        hash.Should().Be(RefreshTokenHasher.ComputeHash(plaintext));

        // The plaintext is URL-safe Base64 without padding (RFC 4648 §5).
        plaintext.Should().NotContain("=").And.NotContain("+").And.NotContain("/");
    }

    [Fact]
    public void Should_GenerateDistinctPlaintexts_When_GeneratingRefreshTokenMaterialTwice()
    {
        var keys = new Mock<ISigningKeyService>(MockBehavior.Strict);
        var sut = CreateSut(keys.Object);

        (string a, _) = sut.GenerateRefreshTokenMaterial();
        (string b, _) = sut.GenerateRefreshTokenMaterial();

        a.Should().NotBe(b);
    }
}
