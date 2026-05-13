using System;

namespace Heimdall.Core.Tokens;

/// <summary>
/// JWS / JWA signing algorithms permitted by Heimdall (Phase 5.1).
/// Mirrors the <c>signing_keys.alg</c> <c>CHECK</c> constraint in
/// <see cref="Heimdall.DAL.Migrations.M202605130001_CreateSigningKeys"/>. HMAC families
/// (<c>HS*</c>) and the <c>none</c> algorithm are deliberately excluded — see
/// <c>docs/proposals/security-and-authorization.md</c> §5.1 and
/// <see href="https://datatracker.ietf.org/doc/html/rfc8725#section-3.1">RFC 8725 §3.1</see>.
/// </summary>
public enum SigningAlgorithm
{
    /// <summary>
    /// RSASSA-PKCS1-v1_5 using SHA-256 over a 2048-bit RSA key.
    /// JWA name: <c>RS256</c>.
    /// </summary>
    Rs256,

    /// <summary>
    /// ECDSA using the NIST P-256 curve and SHA-256.
    /// JWA name: <c>ES256</c>.
    /// </summary>
    Es256,
}

/// <summary>
/// Extension helpers for <see cref="SigningAlgorithm"/> that translate to and from the
/// canonical JWA wire names (<c>"RS256"</c>, <c>"ES256"</c>) stored in
/// <c>signing_keys.alg</c> and published in JWT headers and JWKS documents.
/// </summary>
public static class SigningAlgorithmExtensions
{
    /// <summary>
    /// Returns the canonical JWA name for <paramref name="alg"/>. Matches the
    /// <c>signing_keys.alg</c> <c>CHECK</c> constraint values.
    /// </summary>
    /// <param name="alg">The algorithm to translate.</param>
    /// <returns><c>"RS256"</c> or <c>"ES256"</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="alg"/> is not a defined member of <see cref="SigningAlgorithm"/>.
    /// </exception>
    public static string ToJwaName(this SigningAlgorithm alg) => alg switch
    {
        SigningAlgorithm.Rs256 => "RS256",
        SigningAlgorithm.Es256 => "ES256",
        _ => throw new ArgumentOutOfRangeException(nameof(alg), alg, "Unknown signing algorithm."),
    };

    /// <summary>
    /// Parses a JWA name (e.g. as read back from <c>signing_keys.alg</c>) into a
    /// <see cref="SigningAlgorithm"/>. Case-sensitive: only <c>"RS256"</c> and
    /// <c>"ES256"</c> are accepted — the migration's <c>CHECK</c> constraint already
    /// rejects any other value at write time.
    /// </summary>
    /// <param name="jwaName">The JWA name to parse.</param>
    /// <param name="alg">On success, the parsed algorithm.</param>
    /// <returns><c>true</c> if <paramref name="jwaName"/> mapped to a known algorithm.</returns>
    public static bool TryParseJwaName(string? jwaName, out SigningAlgorithm alg)
    {
        switch (jwaName)
        {
            case "RS256":
                alg = SigningAlgorithm.Rs256;
                return true;
            case "ES256":
                alg = SigningAlgorithm.Es256;
                return true;
            default:
                alg = default;
                return false;
        }
    }
}
