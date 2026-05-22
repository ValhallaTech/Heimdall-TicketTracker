using System;
using System.Security.Cryptography;
using System.Text;

namespace Heimdall.BLL.Tokens;

/// <summary>
/// Phase 5.3 step 7 / Phase 5.4 step 10 — pure-CPU helper that hashes a refresh-token
/// plaintext into the deterministic SHA-256 hex digest stored in
/// <c>refresh_tokens.token_hash</c>. Extracted as a sibling type (rather than buried
/// inside <see cref="JwtTokenIssuer"/>) so the refresh endpoint can hash a presented
/// cookie value without taking a dependency on the issuer, and so the test specialist
/// can drive both call sites through one seam.
/// </summary>
/// <remarks>
/// The hash is deterministic by design — refresh tokens are server-generated 256-bit
/// secrets, so the equality-based <c>GetByHashAsync</c> lookup and the
/// <c>UNIQUE (token_hash)</c> constraint are correct (security-and-authorization
/// §5.2; <see cref="Heimdall.DAL.Repositories.IRefreshTokenRepository"/> XML doc).
/// </remarks>
public static class RefreshTokenHasher
{
    /// <summary>
    /// Returns the lowercase SHA-256 hex digest of the UTF-8 bytes of
    /// <paramref name="plaintext"/>.
    /// </summary>
    /// <param name="plaintext">The refresh-token plaintext (as returned by <see cref="ITokenIssuer.GenerateRefreshTokenMaterial"/>).</param>
    /// <returns>The 64-character lowercase hex digest.</returns>
    /// <exception cref="ArgumentException"><paramref name="plaintext"/> is null/empty/whitespace.</exception>
    public static string ComputeHash(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);

        byte[] utf8 = Encoding.UTF8.GetBytes(plaintext);
        byte[] hash = SHA256.HashData(utf8);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
