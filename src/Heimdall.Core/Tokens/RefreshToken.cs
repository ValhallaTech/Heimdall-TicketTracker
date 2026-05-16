using System;

namespace Heimdall.Core.Tokens;

/// <summary>
/// Strongly-typed projection of a single <c>refresh_tokens</c> row created by
/// <c>M202605200001_CreateRefreshTokens</c>. One row per refresh token; rotation
/// links members of a family through <see cref="ParentId"/> and
/// <see cref="ReplacedBy"/> so the Phase 5.2 step-10 family-replay detector can
/// sweep an entire chain on the first observed reuse of a previously-rotated
/// token. The PBKDF2 hash stored in <see cref="TokenHash"/> is the only form of
/// the token persisted server-side — the plaintext token never leaves the issue
/// path. See <c>docs/proposals/security-and-authorization.md</c> §5.1 / §5.2 and
/// <c>docs/implementation/phase-5-checklist.md</c> Phase 5.2 step 5.
/// </summary>
/// <param name="Id">Primary key. Caller-supplied (the migration does not default it).</param>
/// <param name="UserId">FK to <c>users(id)</c>; cascades on user delete.</param>
/// <param name="TokenHash">
/// PBKDF2 hash of the opaque refresh token (the string emitted by
/// <c>IPasswordHasher&lt;HeimdallUser&gt;</c>). Never the plaintext.
/// </param>
/// <param name="FamilyId">
/// Stable identifier shared by every row in a rotation chain. Used by the
/// step-10 family-replay sweeper (<c>RevokeFamilyAsync</c>).
/// </param>
/// <param name="ParentId">
/// The row this token rotated from, or <c>null</c> on the family-root row.
/// FK back to <c>refresh_tokens(id)</c> with <c>ON DELETE SET NULL</c>.
/// </param>
/// <param name="ReplacedBy">
/// The row that rotated this token, or <c>null</c> if the token is still
/// active (or was revoked without being rotated, e.g. logout). FK back to
/// <c>refresh_tokens(id)</c> with <c>ON DELETE SET NULL</c>.
/// </param>
/// <param name="IssuedAt">UTC instant the row was inserted.</param>
/// <param name="ExpiresAt">UTC instant after which the token must not be accepted.</param>
/// <param name="RevokedAt">
/// UTC instant the token was revoked (rotated, logged out, swept as a family
/// replay, or revoked by an admin), or <c>null</c> while the token is active.
/// </param>
/// <param name="RevokedReason">
/// One of the values defined by <see cref="RefreshTokenRevokedReason"/>. The
/// <c>CHECK</c> constraint on the column rejects anything else.
/// </param>
public sealed record RefreshToken(
    Guid Id,
    Guid UserId,
    string TokenHash,
    Guid FamilyId,
    Guid? ParentId,
    Guid? ReplacedBy,
    DateTime IssuedAt,
    DateTime ExpiresAt,
    DateTime? RevokedAt,
    string? RevokedReason);
