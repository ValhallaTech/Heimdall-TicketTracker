using System;

namespace Heimdall.Core.Tokens;

/// <summary>
/// Public, non-secret view of a <c>signing_keys</c> row. Materialised by
/// <c>ISigningKeyRepository</c> from the read-side query and surfaced by
/// <see cref="ISigningKeyService"/>. Deliberately omits the
/// <c>private_key_protected</c> column — the only path that decrypts the private half
/// is <c>ISigningKeyService.GetCurrentSigningCredentialsAsync</c>, which returns a
/// short-lived <see cref="SigningCredentialsResult"/> instead of a record. Phase 5.1
/// hardening §2.1 requirement.
/// </summary>
/// <param name="Kid">Stable key identifier (published in JWT headers and JWKS).</param>
/// <param name="Alg">JWA algorithm name (<c>"RS256"</c> or <c>"ES256"</c>).</param>
/// <param name="PublicJwk">The deserialised public JWK (no private fields).</param>
/// <param name="NotBefore">The instant the key becomes valid for signing/verification.</param>
/// <param name="NotAfter">The instant the key stops being trusted.</param>
/// <param name="RetiredAt">When set, the key has been retired (no longer used to sign new tokens; still valid for verify until <paramref name="NotAfter"/>).</param>
/// <param name="CreatedAt">Database insert time (<c>signing_keys.created_at</c>).</param>
public sealed record SigningKeyRecord(
    string Kid,
    SigningAlgorithm Alg,
    PublicJwk PublicJwk,
    DateTime NotBefore,
    DateTime NotAfter,
    DateTime? RetiredAt,
    DateTime CreatedAt);
