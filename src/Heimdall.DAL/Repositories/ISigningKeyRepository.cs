using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Tokens;

namespace Heimdall.DAL.Repositories;

/// <summary>
/// Phase 5.1 step 2 — data-access surface for the <c>signing_keys</c> table created by
/// <see cref="Heimdall.DAL.Migrations.M202605130001_CreateSigningKeys"/>. Writes to and
/// reads from <c>private_key_protected</c> are routed through the
/// <c>signing_keys_insert</c> and <c>signing_keys_read_private</c>
/// <c>SECURITY DEFINER</c> functions because the application connection
/// (<c>heimdall_app</c>) holds no direct column privilege on the ciphertext column.
/// Public-data queries (<see cref="GetCurrentAsync"/>, <see cref="GetTrustedAsync"/>,
/// <see cref="UpdateRetiredAtAsync"/>) hit the table directly.
/// </summary>
public interface ISigningKeyRepository
{
    /// <summary>
    /// Inserts a new row via the <c>signing_keys_insert(text, text, jsonb, bytea,
    /// timestamptz, timestamptz)</c> <c>SECURITY DEFINER</c> function.
    /// </summary>
    /// <param name="kid">The stable key identifier (primary key).</param>
    /// <param name="alg">The JWA algorithm name (<c>"RS256"</c> / <c>"ES256"</c>; the <c>CHECK</c> constraint rejects any other value).</param>
    /// <param name="publicJwkJson">The serialised public JWK; bound as <see cref="string"/> with a <c>::jsonb</c> cast.</param>
    /// <param name="privateKeyProtected">PKCS#8 bytes wrapped by ASP.NET Core Data Protection under purpose <c>"Heimdall.JwtSigningKeys.v1"</c>.</param>
    /// <param name="notBefore">UTC <c>not_before</c>.</param>
    /// <param name="notAfter">UTC <c>not_after</c>.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task InsertAsync(
        string kid,
        string alg,
        string publicJwkJson,
        byte[] privateKeyProtected,
        DateTime notBefore,
        DateTime notAfter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads <c>private_key_protected</c> for <paramref name="kid"/> via the
    /// <c>signing_keys_read_private(text)</c> <c>SECURITY DEFINER</c> function.
    /// </summary>
    /// <param name="kid">The key id to read.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The ciphertext, or <c>null</c> if no such row exists.</returns>
    Task<byte[]?> ReadPrivateKeyProtectedAsync(string kid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the newest row where <c>not_before &lt;= now AND not_after &gt; now AND
    /// retired_at IS NULL</c>, projecting only public columns. Used by
    /// <c>ISigningKeyService.GetCurrentSigningKeyAsync</c>.
    /// </summary>
    /// <param name="nowUtc">The reference instant (caller-supplied so the test clock is honoured).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The newest active key, or <c>null</c> if none.</returns>
    Task<SigningKeyRecord?> GetCurrentAsync(DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every row where <c>not_after &gt; now AND retired_at IS NULL</c> —
    /// the trusted public-key set published in the JWKS.
    /// </summary>
    /// <param name="nowUtc">The reference instant.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<IReadOnlyList<SigningKeyRecord>> GetTrustedAsync(DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets <c>retired_at = @retiredAt</c> for the supplied <paramref name="kid"/>. No-op
    /// if the row is already retired (the <c>WHERE retired_at IS NULL</c> guard makes
    /// re-retire idempotent at zero rows affected).
    /// </summary>
    /// <param name="kid">The kid to retire.</param>
    /// <param name="retiredAt">UTC retire instant.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows updated (1 on first retire; 0 thereafter).</returns>
    Task<int> UpdateRetiredAtAsync(string kid, DateTime retiredAt, CancellationToken cancellationToken = default);
}
