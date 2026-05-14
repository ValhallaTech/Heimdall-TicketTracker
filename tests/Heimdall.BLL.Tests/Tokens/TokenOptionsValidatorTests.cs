using FluentAssertions;
using Heimdall.BLL.Tokens;
using Heimdall.Core.Tokens;
using Microsoft.Extensions.Options;
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

    [Fact]
    public void Validate_succeeds_for_the_default_options()
    {
        // The default ctor must pass validation — otherwise startup would fail with
        // an out-of-the-box configuration.
        var result = Sut.Validate(name: null, options: new TokenOptions());

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_fails_when_access_token_lifetime_is_zero()
    {
        var options = new TokenOptions { AccessTokenLifetime = TimeSpan.Zero };

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
        var options = new TokenOptions
        {
            AccessTokenLifetime = TimeSpan.FromMinutes(15),
            SigningKeyOverlap   = TimeSpan.FromMinutes(5),
            SigningKeyValidity  = TimeSpan.FromDays(90),
        };

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
        var options = new TokenOptions
        {
            AccessTokenLifetime = TimeSpan.FromMinutes(15),
            SigningKeyOverlap   = TimeSpan.FromMinutes(15),
            SigningKeyValidity  = TimeSpan.FromDays(90),
        };

        var result = Sut.Validate(name: null, options: options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_fails_when_validity_is_not_greater_than_overlap()
    {
        // A validity window that is not strictly greater than the required overlap
        // cannot satisfy the rotation invariant — every rotation would be rejected.
        var options = new TokenOptions
        {
            AccessTokenLifetime = TimeSpan.FromMinutes(15),
            SigningKeyOverlap   = TimeSpan.FromMinutes(15),
            SigningKeyValidity  = TimeSpan.FromMinutes(15),
        };

        var result = Sut.Validate(name: null, options: options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("SigningKeyValidity");
    }
}
