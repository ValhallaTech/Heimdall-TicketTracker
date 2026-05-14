using Heimdall.Core.Tokens;
using Microsoft.Extensions.Options;

namespace Heimdall.BLL.Tokens;

/// <summary>
/// Validates <see cref="TokenOptions"/> at startup. Ensures the configured
/// signing-key overlap window is at least one access-token lifetime so the
/// invariant enforced in
/// <c>SigningKeyService.GenerateAsync</c> is also expressible in configuration
/// rather than only at the rotation call site.
/// </summary>
/// <remarks>
/// See <c>docs/proposals/phase-5-signing-key-hardening.md</c> §2.5: tokens in
/// flight must stay verifiable through a rotation, which requires
/// <c>SigningKeyOverlap &gt;= AccessTokenLifetime</c>. A misconfigured floor
/// would silently shrink the safety margin; failing fast at startup is the
/// safer posture.
/// </remarks>
public sealed class TokenOptionsValidator : IValidateOptions<TokenOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, TokenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.AccessTokenLifetime <= System.TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail(
                $"TokenOptions.AccessTokenLifetime must be positive (configured: {options.AccessTokenLifetime}).");
        }

        if (options.SigningKeyOverlap < options.AccessTokenLifetime)
        {
            return ValidateOptionsResult.Fail(
                $"TokenOptions.SigningKeyOverlap ({options.SigningKeyOverlap}) must be at least "
                + $"AccessTokenLifetime ({options.AccessTokenLifetime}) so tokens in flight stay "
                + "verifiable through a signing-key rotation.");
        }

        if (options.SigningKeyValidity <= options.SigningKeyOverlap)
        {
            return ValidateOptionsResult.Fail(
                $"TokenOptions.SigningKeyValidity ({options.SigningKeyValidity}) must be greater than "
                + $"SigningKeyOverlap ({options.SigningKeyOverlap}); otherwise rotations cannot satisfy "
                + "the overlap invariant.");
        }

        return ValidateOptionsResult.Success;
    }
}
