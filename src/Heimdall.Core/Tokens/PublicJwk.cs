using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Heimdall.Core.Tokens;

/// <summary>
/// JSON Web Key (RFC 7517) carrying only the <strong>public</strong> components.
/// The field whitelist required by
/// <c>docs/proposals/phase-5-signing-key-hardening.md</c> §2.3 is enforced at the
/// <em>type level</em> — this record exposes no <c>d</c>, <c>p</c>, <c>q</c>,
/// <c>dp</c>, <c>dq</c>, or <c>qi</c> properties at all, so it is not possible to
/// accidentally serialise the private half of an RSA key, and no <c>d</c> property
/// for the EC private scalar.
/// </summary>
/// <remarks>
/// Optional fields use <see cref="JsonIgnoreCondition.WhenWritingNull"/> on the
/// serializer-options level, so an RSA key serialises without <c>crv/x/y</c> and an
/// EC key serialises without <c>n/e</c>.
/// </remarks>
public sealed record PublicJwk
{
    /// <summary>Gets the key type — <c>"RSA"</c> or <c>"EC"</c> (RFC 7518 §6.1).</summary>
    [JsonPropertyName("kty")]
    public required string Kty { get; init; }

    /// <summary>Gets the intended use — always <c>"sig"</c> for Heimdall JWT signing keys (RFC 7517 §4.2).</summary>
    [JsonPropertyName("use")]
    public string Use { get; init; } = "sig";

    /// <summary>Gets the stable key identifier published in JWT headers and in the JWKS (RFC 7517 §4.5).</summary>
    [JsonPropertyName("kid")]
    public required string Kid { get; init; }

    /// <summary>Gets the JWA algorithm name (<c>"RS256"</c> or <c>"ES256"</c>; RFC 7517 §4.4).</summary>
    [JsonPropertyName("alg")]
    public required string Alg { get; init; }

    // ---- RSA public components (RFC 7518 §6.3.1) ---------------------------

    /// <summary>Gets the RSA modulus, Base64URL-encoded. Present only for RSA keys.</summary>
    [JsonPropertyName("n")]
    public string? N { get; init; }

    /// <summary>Gets the RSA public exponent, Base64URL-encoded. Present only for RSA keys.</summary>
    [JsonPropertyName("e")]
    public string? E { get; init; }

    // ---- EC public components (RFC 7518 §6.2.1) ----------------------------

    /// <summary>Gets the EC curve name — always <c>"P-256"</c> for Heimdall ES256 keys. Present only for EC keys.</summary>
    [JsonPropertyName("crv")]
    public string? Crv { get; init; }

    /// <summary>Gets the EC public point x-coordinate, Base64URL-encoded. Present only for EC keys.</summary>
    [JsonPropertyName("x")]
    public string? X { get; init; }

    /// <summary>Gets the EC public point y-coordinate, Base64URL-encoded. Present only for EC keys.</summary>
    [JsonPropertyName("y")]
    public string? Y { get; init; }

    /// <summary>
    /// Builds a <see cref="PublicJwk"/> from an RSA public key. Uses
    /// <see cref="RSA.ExportParameters(bool)"/> with <c>includePrivateParameters: false</c>
    /// so private components never leave the <see cref="RSA"/> instance.
    /// </summary>
    /// <param name="rsa">The RSA key (only the public half is read).</param>
    /// <param name="kid">The stable <c>kid</c> value.</param>
    /// <param name="alg">The JWA algorithm name (e.g. <c>"RS256"</c>).</param>
    /// <returns>The constructed JWK.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rsa"/> is <c>null</c>.</exception>
    public static PublicJwk FromRsa(RSA rsa, string kid, string alg)
    {
        ArgumentNullException.ThrowIfNull(rsa);
        ArgumentException.ThrowIfNullOrWhiteSpace(kid);
        ArgumentException.ThrowIfNullOrWhiteSpace(alg);

        RSAParameters p = rsa.ExportParameters(includePrivateParameters: false);
        return new PublicJwk
        {
            Kty = "RSA",
            Kid = kid,
            Alg = alg,
            N = Base64UrlEncode(p.Modulus!),
            E = Base64UrlEncode(p.Exponent!),
        };
    }

    /// <summary>
    /// Builds a <see cref="PublicJwk"/> from an EC public key. Hard-codes
    /// <c>crv = "P-256"</c> because that is the only EC curve supported by Heimdall
    /// (Phase 5.1 only enrols ES256 keys on P-256).
    /// </summary>
    /// <param name="ecdsa">The ECDSA key (only the public half is read).</param>
    /// <param name="kid">The stable <c>kid</c> value.</param>
    /// <param name="alg">The JWA algorithm name (e.g. <c>"ES256"</c>).</param>
    /// <returns>The constructed JWK.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ecdsa"/> is <c>null</c>.</exception>
    public static PublicJwk FromEcdsa(ECDsa ecdsa, string kid, string alg)
    {
        ArgumentNullException.ThrowIfNull(ecdsa);
        ArgumentException.ThrowIfNullOrWhiteSpace(kid);
        ArgumentException.ThrowIfNullOrWhiteSpace(alg);

        ECParameters p = ecdsa.ExportParameters(includePrivateParameters: false);
        return new PublicJwk
        {
            Kty = "EC",
            Kid = kid,
            Alg = alg,
            Crv = "P-256",
            X = Base64UrlEncode(p.Q.X!),
            Y = Base64UrlEncode(p.Q.Y!),
        };
    }

    /// <summary>
    /// Base64URL-encodes a byte buffer without padding (RFC 7515 §2 "base64url").
    /// </summary>
    private static string Base64UrlEncode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Base64Url.EncodeToString(data);
    }
}

/// <summary>
/// JSON Web Key Set (RFC 7517 §5). The Phase 5.1 JWKS endpoint serialises an instance
/// of this record; every entry passes through the <see cref="PublicJwk"/> field
/// whitelist by construction.
/// </summary>
public sealed record JwkSet
{
    /// <summary>Gets the trusted public keys (current signing key plus any in-overlap predecessors).</summary>
    [JsonPropertyName("keys")]
    public required IReadOnlyList<PublicJwk> Keys { get; init; }
}
