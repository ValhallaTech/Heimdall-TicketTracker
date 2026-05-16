using System;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Tokens;

namespace Heimdall.DAL.Repositories;

/// <summary>
/// Phase 5.2 step 5 — data-access surface for the <c>refresh_tokens</c> table
/// created by <see cref="Heimdall.DAL.Migrations.M202605200001_CreateRefreshTokens"/>.
/// Unlike <c>signing_keys</c> (Phase 5.1, hardened with a two-role +
/// <c>SECURITY DEFINER</c> + RLS posture), <c>refresh_tokens</c> stores only the
/// PBKDF2 hash of each token, so the application connection (<c>heimdall_app</c>)
/// holds direct table-level CRUD privileges and the repository talks to the
/// table without going through any function indirection.
/// </summary>
public interface IRefreshTokenRepository
{
    /// <summary>
    /// Inserts a single <c>refresh_tokens</c> row. All identifiers
    /// (<see cref="RefreshToken.Id"/>, <see cref="RefreshToken.FamilyId"/>,
    /// <see cref="RefreshToken.ParentId"/>, …) are supplied by the caller — the
    /// migration intentionally does not default them.
    /// </summary>
    /// <param name="row">The row to insert. <see cref="RefreshToken.TokenHash"/> must be the PBKDF2 hash, never the plaintext.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task InsertAsync(RefreshToken row, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up a row by its <c>token_hash</c>. Returns the full row including
    /// <c>revoked_at</c> and <c>replaced_by</c> so the caller can detect a replay
    /// attempt (a token presented after it was already rotated).
    /// </summary>
    /// <param name="tokenHash">The PBKDF2 hash of the presented refresh token.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The matching row, or <c>null</c> when no such row exists.</returns>
    Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically rotates an existing refresh token to a successor row. Runs as a
    /// single <see cref="System.Data.IsolationLevel.ReadCommitted"/> transaction:
    /// <list type="number">
    ///   <item>
    ///     <description>
    ///       Insert <paramref name="newRow"/> speculatively. The new row is inserted
    ///       <strong>before</strong> the rotation <c>UPDATE</c> because the
    ///       <c>replaced_by</c> foreign key is not <c>DEFERRABLE</c> — pointing it at a
    ///       not-yet-inserted id would fail at <c>UPDATE</c> statement-end with
    ///       <c>SQLSTATE 23503</c>.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <c>UPDATE refresh_tokens SET revoked_at = now(), revoked_reason = 'rotated',
    ///       replaced_by = @NewId WHERE id = @OldId AND revoked_at IS NULL RETURNING id;</c>
    ///       — if zero rows are returned the old row was already revoked (a replay
    ///       attempt or a lost rotation race); the transaction is rolled back, which
    ///       also rolls back the speculative <c>INSERT</c> from step 1, and the method
    ///       returns <c>false</c>. Otherwise the transaction is committed and the
    ///       method returns <c>true</c>.
    ///     </description>
    ///   </item>
    /// </list>
    /// </summary>
    /// <param name="oldId">Primary key of the row being rotated.</param>
    /// <param name="newRow">The successor row to insert on success.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// <c>true</c> if the rotation succeeded, <c>false</c> if the old row was already
    /// revoked (the caller treats this as a family-replay signal and follows up with
    /// <see cref="RevokeFamilyAsync"/>).
    /// </returns>
    Task<bool> RotateAsync(Guid oldId, RefreshToken newRow, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk-revokes every still-active row in a family with
    /// <c>UPDATE refresh_tokens SET revoked_at = now(), revoked_reason = @Reason
    /// WHERE family_id = @FamilyId AND revoked_at IS NULL;</c>. Used by the
    /// step-10 family-replay sweeper and by the logout path.
    /// </summary>
    /// <param name="familyId">The family to revoke.</param>
    /// <param name="reason">
    /// One of the values defined by <see cref="RefreshTokenRevokedReason"/>.
    /// Validated against the allow-list before binding so a future caller bug
    /// cannot surface the database-side <c>CHECK</c> violation (or worse, smuggle
    /// an arbitrary string in through an unparameterised path).
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows revoked.</returns>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is null/whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is not one of the <see cref="RefreshTokenRevokedReason"/> constants.</exception>
    Task<int> RevokeFamilyAsync(Guid familyId, string reason, CancellationToken cancellationToken = default);
}
