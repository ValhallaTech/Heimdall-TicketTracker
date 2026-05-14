using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Tokens;
using Heimdall.DAL.Repositories;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Heimdall.BLL.Tokens;

/// <summary>
/// Phase 5.1 step 2 (<c>docs/implementation/phase-5-checklist.md</c>) — implements the
/// hardening rules from <c>docs/proposals/phase-5-signing-key-hardening.md</c> §2.1
/// (envelope encryption via a purpose-isolated <see cref="IDataProtector"/>,
/// <see cref="CryptographicOperations.ZeroMemory(System.Span{byte})"/> on every plaintext
/// PKCS#8 buffer, never cache the decrypted private key), §2.3 (memory-cached public
/// metadata only; invalidate on rotation), and §2.5 (reject rotations whose overlap
/// window is shorter than one access-token lifetime).
/// </summary>
public sealed class SigningKeyService : ISigningKeyService
{
    /// <summary>
    /// Purpose string for the Data Protection protector. Pinned here and in the
    /// <c>signing_keys.private_key_protected</c> column comment so the two sources
    /// cannot drift; changing the value rotates the entire envelope.
    /// </summary>
    private const string ProtectorPurpose = "Heimdall.JwtSigningKeys.v1";

    private const string CurrentKeyCacheKey = "signing-keys:current:v1";
    private const string TrustedKeysCacheKey = "signing-keys:trusted:v1";
    private static readonly TimeSpan MetadataCacheTtl = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JwkJsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ISigningKeyRepository _repository;
    private readonly IDataProtector _keyProtector;
    private readonly IMemoryCache _cache;
    private readonly IJwksCacheInvalidator _jwksInvalidator;
    private readonly IOptions<TokenOptions> _options;
    private readonly ILogger<SigningKeyService> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="SigningKeyService"/> class.
    /// </summary>
    /// <param name="repository">The signing-key data-access surface.</param>
    /// <param name="dpProvider">The shared <see cref="IDataProtectionProvider"/> wired in Phase 1 step 4.</param>
    /// <param name="cache">The in-process memory cache (shared with the JWKS endpoint).</param>
    /// <param name="jwksInvalidator">Seam used to invalidate the JWKS document on rotation.</param>
    /// <param name="options">Bound <see cref="TokenOptions"/>.</param>
    /// <param name="logger">Structured logger.</param>
    /// <param name="timeProvider">Optional time source; defaults to <see cref="TimeProvider.System"/>.</param>
    public SigningKeyService(
        ISigningKeyRepository repository,
        IDataProtectionProvider dpProvider,
        IMemoryCache cache,
        IJwksCacheInvalidator jwksInvalidator,
        IOptions<TokenOptions> options,
        ILogger<SigningKeyService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(dpProvider);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(jwksInvalidator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _repository = repository;
        _keyProtector = dpProvider.CreateProtector(ProtectorPurpose);
        _cache = cache;
        _jwksInvalidator = jwksInvalidator;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<string> GenerateAsync(
        SigningAlgorithm alg,
        TimeSpan validity,
        CancellationToken ct = default)
    {
        if (validity <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(validity), validity, "Signing-key validity must be a positive duration.");
        }

        DateTime notBefore = _timeProvider.GetUtcNow().UtcDateTime;
        DateTime notAfter = notBefore + validity;

        // Overlap-window enforcement (hardening §2.5). The current key, if any, must
        // remain valid for at least one access-token lifetime after the new key takes
        // effect so tokens in flight stay verifiable across the cutover. The configured
        // floor is TokenOptions.SigningKeyOverlap; TokenOptionsValidator (registered at
        // startup) guarantees SigningKeyOverlap >= AccessTokenLifetime so this check
        // remains at least as strict as one access-token TTL.
        SigningKeyRecord? current = await _repository
            .GetCurrentAsync(notBefore, ct)
            .ConfigureAwait(false);
        if (current is not null && current.NotAfter > notBefore)
        {
            TimeSpan overlap = current.NotAfter - notBefore;
            TimeSpan required = _options.Value.SigningKeyOverlap;
            if (overlap < required)
            {
                throw new InvalidOperationException(
                    $"Rotation rejected: overlap window {overlap} is shorter than the configured "
                    + $"SigningKeyOverlap {required}. Extend the outgoing key's not_after before rotating.");
            }
        }

        string kid = Guid.NewGuid().ToString("N");
        string jwaName = alg.ToJwaName();

        PublicJwk jwk;
        byte[] pkcs8;
        switch (alg)
        {
            case SigningAlgorithm.Rs256:
            {
                using var rsa = RSA.Create(2048);
                jwk = PublicJwk.FromRsa(rsa, kid, jwaName);
                pkcs8 = rsa.ExportPkcs8PrivateKey();
                break;
            }

            case SigningAlgorithm.Es256:
            {
                using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                jwk = PublicJwk.FromEcdsa(ecdsa, kid, jwaName);
                pkcs8 = ecdsa.ExportPkcs8PrivateKey();
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(alg), alg, "Unsupported signing algorithm.");
        }

        byte[] ciphertext;
        try
        {
            ciphertext = _keyProtector.Protect(pkcs8);
        }
        finally
        {
            // Always zero the plaintext PKCS#8 buffer — hardening §2.1 explicit requirement.
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        string jwkJson = JsonSerializer.Serialize(jwk, JwkJsonOptions);

        await _repository
            .InsertAsync(kid, jwaName, jwkJson, ciphertext, notBefore, notAfter, ct)
            .ConfigureAwait(false);

        // Invalidate metadata + JWKS caches after the DB write succeeds — readers will
        // re-materialise on the next call and see the new key immediately.
        _cache.Remove(CurrentKeyCacheKey);
        _cache.Remove(TrustedKeysCacheKey);
        _jwksInvalidator.Invalidate();

        _logger.LogInformation(
            "Generated signing key {Kid} alg={Alg} notBefore={NotBefore:o} notAfter={NotAfter:o}.",
            kid, jwaName, notBefore, notAfter);

        return kid;
    }

    /// <inheritdoc />
    public async Task<SigningKeyRecord?> GetCurrentSigningKeyAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CurrentKeyCacheKey, out SigningKeyRecord? cached))
        {
            return cached;
        }

        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        SigningKeyRecord? record = await _repository.GetCurrentAsync(now, ct).ConfigureAwait(false);

        // Cache both hits and misses for a short window to bound DB load. A miss is
        // cheap to re-check, so the negative cache mirrors the positive TTL.
        _cache.Set(CurrentKeyCacheKey, record, MetadataCacheTtl);
        return record;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SigningKeyRecord>> GetTrustedKeysAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(TrustedKeysCacheKey, out IReadOnlyList<SigningKeyRecord>? cached) && cached is not null)
        {
            return cached;
        }

        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        IReadOnlyList<SigningKeyRecord> rows = await _repository.GetTrustedAsync(now, ct).ConfigureAwait(false);
        _cache.Set(TrustedKeysCacheKey, rows, MetadataCacheTtl);
        return rows;
    }

    /// <inheritdoc />
    public async Task RetireAsync(string kid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kid);

        DateTime retiredAt = _timeProvider.GetUtcNow().UtcDateTime;
        int affected = await _repository
            .UpdateRetiredAtAsync(kid, retiredAt, ct)
            .ConfigureAwait(false);

        if (affected > 0)
        {
            _cache.Remove(CurrentKeyCacheKey);
            _cache.Remove(TrustedKeysCacheKey);
            _jwksInvalidator.Invalidate();
            _logger.LogInformation("Retired signing key {Kid} at {RetiredAt:o}.", kid, retiredAt);
        }
        else
        {
            _logger.LogInformation(
                "Retire requested for signing key {Kid} but the row was already retired or absent.", kid);
        }
    }

    /// <summary>
    /// Returns a fresh <see cref="SigningCredentialsResult"/> per call. The decrypted
    /// key is request-scoped and disposed when the result is disposed. Do NOT store
    /// the result in a field — the lifetime guarantee is per-method-call.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A short-lived signing-credentials handle.</returns>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership transfers to the returned SigningCredentialsResult; caller disposes via using-block (Phase 5.1 hardening §2.1).")]
    public async Task<SigningCredentialsResult> GetCurrentSigningCredentialsAsync(CancellationToken ct = default)
    {
        SigningKeyRecord? record = await GetCurrentSigningKeyAsync(ct).ConfigureAwait(false);
        if (record is null)
        {
            throw new InvalidOperationException("No active signing key.");
        }

        byte[]? ciphertext = await _repository
            .ReadPrivateKeyProtectedAsync(record.Kid, ct)
            .ConfigureAwait(false);
        if (ciphertext is null || ciphertext.Length == 0)
        {
            throw new InvalidOperationException(
                $"private_key_protected was not readable for kid {record.Kid}; the SECURITY DEFINER reader returned NULL.");
        }

        byte[] pkcs8;
        try
        {
            pkcs8 = _keyProtector.Unprotect(ciphertext);
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(
                ex,
                "Signing key decrypt failed for kid {Kid}; DP key ring may be revoked or the ciphertext tampered.",
                record.Kid);
            throw;
        }

        AsymmetricAlgorithm? key = null;
        try
        {
            try
            {
                key = record.Alg switch
                {
                    SigningAlgorithm.Rs256 => ImportRsa(pkcs8),
                    SigningAlgorithm.Es256 => ImportEcdsa(pkcs8),
                    _ => throw new InvalidOperationException(
                        $"Unsupported alg {record.Alg} on kid {record.Kid}."),
                };
            }
            catch (CryptographicException ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to import PKCS#8 for kid {Kid} after a successful Unprotect.",
                    record.Kid);
                throw;
            }

            return new SigningCredentialsResult(record.Kid, record.Alg, key);
        }
        catch
        {
            key?.Dispose();
            throw;
        }
        finally
        {
            // Zero the plaintext PKCS#8 buffer regardless of whether import succeeded —
            // it must not linger on the managed heap.
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    private static RSA ImportRsa(byte[] pkcs8)
    {
        var rsa = RSA.Create();
        try
        {
            rsa.ImportPkcs8PrivateKey(pkcs8, out _);
            return rsa;
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    private static ECDsa ImportEcdsa(byte[] pkcs8)
    {
        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportPkcs8PrivateKey(pkcs8, out _);
            return ecdsa;
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }
    }
}
