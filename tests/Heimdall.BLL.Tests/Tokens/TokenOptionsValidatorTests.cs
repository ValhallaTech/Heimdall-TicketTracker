using FluentAssertions;
using Heimdall.BLL.Tokens;
using Heimdall.Core.Tokens;
using Xunit;

namespace Heimdall.BLL.Tests.Tokens;

/// <summary>
/// Pins the startup-time invariants that the runtime enforcement in
/// <see cref="SigningKeyService.GenerateAsync"/> relies on. Each case maps to a
/// branch in <see cref="TokenOptionsValidator.Validate"/>.
/// </summary>
public sealed class TokenOptionsValidatorTests
{
    private static readonly TokenOptionsValidator Sut = new();

    /// <summary>
    /// Builds a fully-populated <see cref="TokenOptions"/> equivalent to the bound
    /// production configuration. Tests that exercise a single property override the
    /// one they care about, keeping the rest realistic.
    /// </summary>
    private static TokenOptions ValidOptions() => new()
    {
        AccessTokenLifetime  = TimeSpan.FromMinutes(15),
        SigningKeyOverlap    = TimeSpan.FromMinutes(15),
        SigningKeyValidity   = TimeSpan.FromDays(90),
        RefreshTokenLifetime = TimeSpan.FromDays(14),
        Issuer   = "https://heimdall.local",
        Audience = "heimdall.api",
    };

    [Fact]
    public void Validate_succeeds_for_a_fully_populated_options_instance()
    {
        // Phase 5.3 step 6 made Issuer/Audience required, so the default-ctor
        // variant no longer passes — see Validate_fails_when_issuer_is_empty.
        var result = Sut.Validate(name: null, options: ValidOptions());

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_fails_when_access_token_lifetime_is_zero()
    {
        var options = ValidOptions();
        options.AccessTokenLifetime = TimeSpan.Zero;

        var result = Sut.Validate(name: null, options: options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("AccessTokenLifetime");
    }

    [Fact]
    public void Validate_fails_when_overlap_is_shorter_than_access_token_lifetime()
    {
        // Hardening §2.5: an overlap smaller than one access-token TTL would leave
        // tokens in flight unverifiable through a rotation. ValidateOnStart must
        // reject this configuration before any signing-key call.
        var options = ValidOptions();
        options.AccessTokenLifetime = TimeSpan.FromMinutes(15);
        options.SigningKeyOverlap   = TimeSpan.FromMinutes(5);
        options.SigningKeyValidity  = TimeSpan.FromDays(90);

        var result = Sut.Validate(name: null, options: options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("SigningKeyOverlap");
        result.FailureMessage.Should().Contain("AccessTokenLifetime");
    }

    [Fact]
    public void Validate_succeeds_when_overlap_equals_access_token_lifetime()
    {
        // Equality is the boundary: SigningKeyService.GenerateAsync uses `overlap <
        // required`, so SigningKeyOverlap == AccessTokenLifetime must be allowed.
        var options = ValidOptions();
        options.AccessTokenLifetime = TimeSpan.FromMinutes(15);
        options.SigningKeyOverlap   = TimeSpan.FromMinutes(15);
        options.SigningKeyValidity  = TimeSpan.FromDays(90);

        var result = Sut.Validate(name: null, options: options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_fails_when_validity_is_not_greater_than_overlap()
    {
        // A validity window that is not strictly greater than the required overlap
        // cannot satisfy the rotation invariant — every rotation would be rejected.
        var options = ValidOptions();
        options.AccessTokenLifetime = TimeSpan.FromMinutes(15);
        options.SigningKeyOverlap   = TimeSpan.FromMinutes(15);
        options.SigningKeyValidity  = TimeSpan.FromMinutes(15);

        var result = Sut.Validate(name: null, options: options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("SigningKeyValidity");
    }

    [Fact]
    public void Validate_fails_when_issuer_is_empty()
    {
        // Phase 5.3 step 6 — JwtBearer requires a non-empty issuer. The validator
        // surfaces this at startup so the diagnostic is a clear options-validation
        // failure rather than a deferred 401 chain.
        var options = ValidOptions();
        options.Issuer = string.Empty;

        var result = Sut.Validate(name: null, options: options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Issuer");
    }

    [Fact]
    public void Validate_fails_when_issuer_is_whitespace()
    {
        var options = ValidOptions();
        options.Issuer = "   ";

        var result = Sut.Validate(name: null, options: options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Issuer");
    }

    [Fact]
    public void Validate_fails_when_audience_is_empty()
    {
        var options = ValidOptions();
        options.Audience = string.Empty;

        var result = Sut.Validate(name: null, options: options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Audience");
    }

    [Fact]
    public void Validate_fails_when_audience_is_whitespace()
    {
        var options = ValidOptions();
        options.Audience = "   ";

        var result = Sut.Validate(name: null, options: options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Audience");
    }
}
