using System;

namespace Heimdall.BLL.Tokens;

/// <summary>
/// Phase 5.3 step 7 (<c>docs/implementation/phase-5-checklist.md</c>) — the result of
/// <see cref="ITokenIssuer.IssueAccessTokenAsync"/>. Carries the serialised JWT plus the
/// non-secret metadata the caller needs for the audit-event payload
/// (<see cref="Heimdall.Core.Auditing.AuditEventTypes.TokenAccessIssued"/>) and the
/// response body's <c>expires_in</c> field.
/// </summary>
/// <param name="Jwt">The compact serialised JWT — opaque to callers other than the wire.</param>
/// <param name="Jti">The token's <c>jti</c> claim. Phase 5.5 step 11 keys the Redis denylist on this.</param>
/// <param name="IssuedAt">The <c>iat</c> instant the token was minted (UTC).</param>
/// <param name="ExpiresAt">The <c>exp</c> instant after which the token must be rejected (UTC).</param>
public sealed record IssuedAccessToken(
    string Jwt,
    string Jti,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);
