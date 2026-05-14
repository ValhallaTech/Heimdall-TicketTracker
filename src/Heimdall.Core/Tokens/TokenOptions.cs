using System;

namespace Heimdall.Core.Tokens;

/// <summary>
/// Strongly-typed options bound to the <c>"Token"</c> configuration section. Carries the
/// minimal knobs required by Phase 5.1 — the signing-key rotation cadence and the overlap
/// window that <see cref="Heimdall.Core.Tokens"/> consumers enforce. JWT-bearer–specific
/// settings (issuer, audience, clock skew, etc.) land in Phase 5.3 step 6 and are
/// deliberately omitted here so this type tracks only what Phase 5.1 actually needs.
/// </summary>
/// <remarks>
/// See <c>docs/proposals/security-and-authorization.md</c> §5.1 (15-minute access tokens)
/// and <c>docs/proposals/phase-5-signing-key-hardening.md</c> §2.5 (overlap-window
/// enforcement: the gap between an outgoing key's <c>not_after</c> and the new key's
/// <c>not_before</c> must be at least one access-token lifetime so tokens in flight stay
/// verifiable across rotation). The Phase 5.1 checklist step 2 references this contract
/// for the overlap-rejection branch in <c>SigningKeyService.GenerateAsync</c>.
/// </remarks>
public class TokenOptions
{
    /// <summary>
    /// Gets or sets the access-token lifetime. Tokens minted by the (Phase 5.3) issuer
    /// will carry <c>exp = iat + AccessTokenLifetime</c>. Used by Phase 5.1 to validate
    /// that the rotation overlap window is at least one access-token lifetime so tokens
    /// in flight remain verifiable for their full lifetime after a rotation.
    /// Default: 15 minutes (security-and-authorization §5.1).
    /// </summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Gets or sets the minimum overlap window between consecutive signing keys.
    /// <c>SigningKeyService.GenerateAsync</c> rejects a rotation whose computed overlap is
    /// shorter than this value. Must be at least <see cref="AccessTokenLifetime"/> so
    /// tokens in flight stay verifiable across rotation — this floor is enforced at
    /// startup by <c>Heimdall.BLL.Tokens.TokenOptionsValidator</c>.
    /// Default: 15 minutes (matches <see cref="AccessTokenLifetime"/>; hardening §2.5).
    /// </summary>
    public TimeSpan SigningKeyOverlap { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Gets or sets the validity (life) of a freshly-generated signing key —
    /// <c>not_after = not_before + SigningKeyValidity</c>. The operator runbook (Phase 5
    /// step 22) drives rotation against this cadence.
    /// Default: 90 days (hardening §2.5 / security-and-authorization §5.4).
    /// </summary>
    public TimeSpan SigningKeyValidity { get; set; } = TimeSpan.FromDays(90);
}
