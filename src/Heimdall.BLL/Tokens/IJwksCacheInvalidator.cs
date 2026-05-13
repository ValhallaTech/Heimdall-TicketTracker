using Microsoft.Extensions.Caching.Memory;

namespace Heimdall.BLL.Tokens;

/// <summary>
/// Seam used by <see cref="SigningKeyService"/> to invalidate the
/// <c>/.well-known/jwks.json</c> response cache when the trusted key set changes
/// (key generated or retired). Phase 5.1 checklist step 3 / hardening §2.3:
/// the JWKS endpoint caches its response in <see cref="IMemoryCache"/> for 5 minutes
/// to bound DB reads, and the service-side rotation must propagate immediately.
/// </summary>
public interface IJwksCacheInvalidator
{
    /// <summary>
    /// Removes the cached JWKS document so the next request re-builds it from the
    /// fresh trusted-key set.
    /// </summary>
    void Invalidate();
}

/// <summary>
/// <see cref="IMemoryCache"/>-backed implementation of <see cref="IJwksCacheInvalidator"/>.
/// Removes the cache key the JWKS endpoint uses (<see cref="CacheKey"/>); construction is
/// stateless so this is safe to register as a singleton.
/// </summary>
public sealed class MemoryCacheJwksCacheInvalidator : IJwksCacheInvalidator
{
    /// <summary>
    /// The exact <see cref="IMemoryCache"/> key used by the JWKS endpoint. Kept here
    /// (and not in the endpoint) so the invalidator and the producer share a single
    /// constant rather than two string literals that might drift apart.
    /// </summary>
    public const string CacheKey = "jwks:document:v1";

    private readonly IMemoryCache _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryCacheJwksCacheInvalidator"/> class.
    /// </summary>
    /// <param name="cache">The shared in-process memory cache.</param>
    /// <exception cref="System.ArgumentNullException"><paramref name="cache"/> is <c>null</c>.</exception>
    public MemoryCacheJwksCacheInvalidator(IMemoryCache cache)
    {
        System.ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    /// <inheritdoc />
    public void Invalidate() => _cache.Remove(CacheKey);
}
