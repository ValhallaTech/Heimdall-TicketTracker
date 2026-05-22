using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Models;

namespace Heimdall.BLL.Tokens;

/// <summary>
/// Phase 5.3 step 7 (<c>docs/implementation/phase-5-checklist.md</c>) — the BLL surface
/// responsible for minting access tokens (JWTs) and generating the random material that
/// backs a refresh-token row. Orchestration of access + refresh issuance (the DB
/// <c>refresh_tokens</c> insert plus the matching audit-event writes) lives in
/// <c>Heimdall.Web.Endpoints.ApiAuthEndpoints</c> per the checklist: this interface is
/// intentionally narrow so the endpoint owns the transactional / audit composition.
/// </summary>
/// <remarks>
/// Claim shape and signing-material lifetime are pinned in
/// <c>docs/proposals/security-and-authorization.md</c> §5.1 and the Phase 5.1
/// hardening proposal §2.1 (no caching of the decrypted private key).
/// </remarks>
public interface ITokenIssuer
{
    /// <summary>
    /// Issues a freshly-signed access token (JWT) for <paramref name="user"/> with the
    /// supplied <paramref name="amr"/> claim set. Resolves the current signing material
    /// via <see cref="ISigningKeyService.GetCurrentSigningCredentialsAsync"/> and
    /// disposes it inside this method — callers must not retain or otherwise observe the
    /// underlying private key. Claims minted: <c>sub</c> (user id), <c>email</c>,
    /// <c>amr</c> (one entry per element of <paramref name="amr"/>),
    /// <c>mfa_enrolled</c> (<c>"true"</c>/<c>"false"</c>), <c>jti</c>, <c>iat</c>,
    /// <c>nbf</c>, <c>exp</c> (<c>exp = iat + TokenOptions.AccessTokenLifetime</c>).
    /// </summary>
    /// <param name="user">The Identity user the token is being minted for. Must not be <c>null</c>.</param>
    /// <param name="amr">
    /// Authentication-methods-references values to mirror into the JWT.
    /// <c>"pwd"</c> always; <c>"mfa"</c> when the cookie principal that triggered the call
    /// already carried it (Phase 4.5 step 12). Must not be <c>null</c> or empty.
    /// </param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The issued access token plus the metadata callers need for the audit row.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="user"/> or <paramref name="amr"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="amr"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// No active signing key exists, or the signing key's algorithm is outside the
    /// permitted set (<c>RS256</c> / <c>ES256</c>; HS-family and <c>none</c> are
    /// rejected at the issue-side per the Phase 5.1 hardening proposal).
    /// </exception>
    Task<IssuedAccessToken> IssueAccessTokenAsync(
        HeimdallUser user,
        IReadOnlyCollection<string> amr,
        CancellationToken ct = default);

    /// <summary>
    /// Synchronously generates the refresh-token plaintext (32 cryptographically-random
    /// bytes, URL-safe Base64 without padding) and its deterministic SHA-256 hex digest
    /// — the value that lands in <c>refresh_tokens.token_hash</c>. Typed as a method
    /// (not a property) to make the allocation cost explicit. The plaintext is the only
    /// value ever returned to the client (set on the <c>__Host-heimdall_refresh</c>
    /// cookie); the hash is the only value ever persisted server-side.
    /// </summary>
    /// <returns>The freshly-generated plaintext and its SHA-256 hex digest.</returns>
    (string PlaintextToken, string TokenHash) GenerateRefreshTokenMaterial();
}
