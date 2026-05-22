using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Models;
using Heimdall.Core.Tokens;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Heimdall.BLL.Tokens;

/// <summary>
/// Phase 5.3 step 7 (<c>docs/implementation/phase-5-checklist.md</c>) implementation
/// of <see cref="ITokenIssuer"/>. Builds JWTs via
/// <see cref="JsonWebTokenHandler.CreateToken(SecurityTokenDescriptor)"/> using the
/// current signing material returned by
/// <see cref="ISigningKeyService.GetCurrentSigningCredentialsAsync"/>; the decrypted
/// private key is held only for the duration of a single
/// <see cref="IssueAccessTokenAsync"/> call (wrapped in <c>using</c>) and disposed before
/// the method returns (hardening proposal §2.1 — "no long-lived <c>SecurityKey</c>").
/// </summary>
/// <remarks>
/// Refresh-token plaintext / hash generation lives here too because both the access-
/// token issuance and the refresh-rotation paths need it, and keeping them in the same
/// type means the random-bytes-and-hash recipe is defined exactly once.
/// </remarks>
public sealed class JwtTokenIssuer : ITokenIssuer
{
    private const int RefreshTokenByteLength = 32;

    private readonly ISigningKeyService _signingKeys;
    private readonly IOptions<TokenOptions> _options;
    private readonly ILogger<JwtTokenIssuer> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly JsonWebTokenHandler _handler;

    /// <summary>
    /// Initializes a new instance of the <see cref="JwtTokenIssuer"/> class.
    /// </summary>
    /// <param name="signingKeys">Resolves the current signing key per call (no caching).</param>
    /// <param name="options">Bound <see cref="TokenOptions"/> — supplies issuer, audience, and lifetime.</param>
    /// <param name="logger">Structured logger.</param>
    /// <param name="timeProvider">Optional time source; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentNullException">Any required dependency is <c>null</c>.</exception>
    public JwtTokenIssuer(
        ISigningKeyService signingKeys,
        IOptions<TokenOptions> options,
        ILogger<JwtTokenIssuer> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(signingKeys);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _signingKeys = signingKeys;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // JsonWebTokenHandler is stateless and safe to share across calls.
        _handler = new JsonWebTokenHandler();
    }

    /// <inheritdoc />
    public async Task<IssuedAccessToken> IssueAccessTokenAsync(
        HeimdallUser user,
        IReadOnlyCollection<string> amr,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(amr);
        if (amr.Count == 0)
        {
            throw new ArgumentException("amr must contain at least one entry (e.g. \"pwd\").", nameof(amr));
        }

        TokenOptions opts = _options.Value;

        DateTimeOffset issuedAt = _timeProvider.GetUtcNow();
        DateTimeOffset expiresAt = issuedAt + opts.AccessTokenLifetime;
        string jti = Guid.NewGuid().ToString("N");

        // SigningCredentialsResult owns the decrypted private key; the using block
        // disposes it before this method returns so the key never outlives the call
        // (hardening §2.1 — "no long-lived SecurityKey").
        using SigningCredentialsResult signing = await _signingKeys
            .GetCurrentSigningCredentialsAsync(ct)
            .ConfigureAwait(false);

        // Defensive: SigningKeyService should never hand us anything outside RS256/ES256
        // (the column CHECK and the enum both enforce it), but the explicit assertion
        // means a misconfigured signing_keys row surfaces as an InvalidOperationException
        // at issue time rather than as an opaque downstream verifier failure.
        if (signing.Alg is not SigningAlgorithm.Rs256 and not SigningAlgorithm.Es256)
        {
            throw new InvalidOperationException(
                $"Refusing to sign: alg {signing.Alg} is outside the permitted RS256/ES256 set.");
        }

        SecurityKey securityKey = signing.Alg switch
        {
            SigningAlgorithm.Rs256 => new RsaSecurityKey((RSA)signing.Key) { KeyId = signing.Kid },
            SigningAlgorithm.Es256 => new ECDsaSecurityKey((ECDsa)signing.Key) { KeyId = signing.Kid },
            _ => throw new InvalidOperationException($"Unsupported alg {signing.Alg}."),
        };

        // Hardening §2.1 ("no long-lived SecurityKey") requires the decrypted
        // private key to be disposed at the end of every issuance call. However,
        // CryptoProviderFactory.Default has CacheSignatureProviders=true and keys
        // its SignatureProvider cache on SecurityKey.KeyId (the stable kid).
        // Without overriding the factory on this SecurityKey instance, the second
        // issuance call would hit a cached SignatureProvider still holding a
        // reference to the *already-disposed* RSA/ECDsa from the first call,
        // throwing ObjectDisposedException. Attaching a per-instance non-caching
        // CryptoProviderFactory forces each issuance to build and release its own
        // SignatureProvider, preserving the §2.1 invariant. DO NOT remove this
        // assignment as "dead code" — see JwtTokenIssuer tests for coverage.
        securityKey.CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false };

        string jwaName = signing.Alg.ToJwaName();
        var credentials = new SigningCredentials(securityKey, jwaName);

        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = user.Id.ToString(),
            [JwtRegisteredClaimNames.Email] = user.Email,
            [JwtRegisteredClaimNames.Jti] = jti,

            // mfa_enrolled mirrors users.two_factor_enabled at issue time. Serialised as
            // the strings "true"/"false" (proposal §5.1) so consumers can do a literal
            // string compare without booleans-vs-string ambiguity in JSON.
            ["mfa_enrolled"] = user.TwoFactorEnabled ? "true" : "false",
        };

        // amr is a JSON array when there is more than one value, or a single string when
        // there is exactly one (matching RFC 8176 conventions); JsonWebTokenHandler
        // serialises a string[] as an array regardless of length, which is acceptable.
        claims["amr"] = new List<string>(amr);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = opts.Issuer,
            Audience = opts.Audience,
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = credentials,
            Claims = claims,
        };

        string jwt = _handler.CreateToken(descriptor);

        _logger.LogInformation(
            "Issued access token {Jti} for user {UserId} alg={Alg} kid={Kid} amrCount={AmrCount}.",
            jti,
            user.Id,
            jwaName,
            signing.Kid,
            amr.Count);

        return new IssuedAccessToken(jwt, jti, issuedAt, expiresAt);
    }

    /// <inheritdoc />
    public (string PlaintextToken, string TokenHash) GenerateRefreshTokenMaterial()
    {
        Span<byte> raw = stackalloc byte[RefreshTokenByteLength];
        RandomNumberGenerator.Fill(raw);

        // URL-safe Base64 without padding — same alphabet as JWT components, safe to
        // round-trip through a cookie value without further escaping.
        string plaintext = Base64UrlEncode(raw);
        string hash = RefreshTokenHasher.ComputeHash(plaintext);
        return (plaintext, hash);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        string b64 = Convert.ToBase64String(bytes);

        // Strip padding and translate the standard alphabet to URL-safe per RFC 4648 §5.
        return b64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
