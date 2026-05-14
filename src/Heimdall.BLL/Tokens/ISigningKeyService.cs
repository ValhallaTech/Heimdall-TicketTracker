using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Tokens;

namespace Heimdall.BLL.Tokens;

/// <summary>
/// Phase 5.1 step 2 (<c>docs/implementation/phase-5-checklist.md</c>) — the BLL surface
/// the rest of the application uses to enrol, list, retire, and sign with JWT signing
/// keys. Every method that touches private material is implemented to satisfy the
/// hardening rules in <c>docs/proposals/phase-5-signing-key-hardening.md</c> §2.1
/// (envelope encryption, <c>ZeroMemory</c>, no caching of the decrypted key) and §2.5
/// (overlap-window enforcement).
/// </summary>
public interface ISigningKeyService
{
    /// <summary>
    /// Generates a new RSA-2048 or P-256 EC key pair, wraps the private half via the
    /// purpose-isolated <see cref="Microsoft.AspNetCore.DataProtection.IDataProtector"/>
    /// (<c>"Heimdall.JwtSigningKeys.v1"</c>), inserts a row through the
    /// <c>signing_keys_insert</c> SECURITY DEFINER function, and returns the new
    /// <c>kid</c>. Rejects the call with <see cref="InvalidOperationException"/> if the
    /// computed overlap with the existing current key would be shorter than
    /// <see cref="TokenOptions.AccessTokenLifetime"/> (hardening §2.5).
    /// </summary>
    /// <param name="alg">The signing algorithm to use.</param>
    /// <param name="validity">The lifetime of the new key — <c>not_after = now + validity</c>.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The <c>kid</c> of the newly inserted row.</returns>
    Task<string> GenerateAsync(SigningAlgorithm alg, TimeSpan validity, CancellationToken ct = default);

    /// <summary>
    /// Returns the newest row where <c>not_before &lt;= now() &lt; not_after AND
    /// retired_at IS NULL</c>. Public-data only — the result never carries
    /// <c>private_key_protected</c>. May be served from a short-lived
    /// <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/> entry that is
    /// invalidated on <see cref="GenerateAsync"/> / <see cref="RetireAsync"/>.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The current signing key, or <c>null</c> if none is active.</returns>
    Task<SigningKeyRecord?> GetCurrentSigningKeyAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns every row where <c>now() &lt; not_after AND retired_at IS NULL</c> —
    /// the set used by the JWKS publisher and by the (Phase 5.3) JWT verifier.
    /// Public-data only.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The trusted public-key set; empty when no keys are active.</returns>
    Task<IReadOnlyList<SigningKeyRecord>> GetTrustedKeysAsync(CancellationToken ct = default);

    /// <summary>
    /// Marks <paramref name="kid"/> as retired (<c>retired_at = now()</c>). Tokens
    /// already in flight remain verifiable until <c>not_after</c>; the key is excluded
    /// from <see cref="GetCurrentSigningKeyAsync"/> immediately.
    /// </summary>
    /// <param name="kid">The kid to retire.</param>
    /// <param name="ct">A cancellation token.</param>
    Task RetireAsync(string kid, CancellationToken ct = default);

    /// <summary>
    /// Returns a fresh <see cref="SigningCredentialsResult"/> backed by a newly-decrypted
    /// <see cref="System.Security.Cryptography.AsymmetricAlgorithm"/>. The decrypted key
    /// is request-scoped and disposed when the result is disposed. <strong>Do NOT</strong>
    /// store the result in a field — the lifetime guarantee is per-method-call; every
    /// call constructs a new <see cref="System.Security.Cryptography.AsymmetricAlgorithm"/>.
    /// Hardening §2.1 requirement.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A short-lived signing-credentials handle.</returns>
    /// <exception cref="InvalidOperationException">No active signing key exists.</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// The Data Protection key ring is unavailable or the ciphertext has been tampered
    /// with. Surfaced unchanged — the service fails closed.
    /// </exception>
    Task<SigningCredentialsResult> GetCurrentSigningCredentialsAsync(CancellationToken ct = default);
}
