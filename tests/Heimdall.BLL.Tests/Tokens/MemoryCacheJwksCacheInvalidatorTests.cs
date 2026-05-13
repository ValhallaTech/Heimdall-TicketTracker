using FluentAssertions;
using Heimdall.BLL.Tokens;
using Microsoft.Extensions.Caching.Memory;

namespace Heimdall.BLL.Tests.Tokens;

/// <summary>
/// Unit tests for <see cref="MemoryCacheJwksCacheInvalidator"/>. The invalidator is the
/// only seam that propagates a rotation to the JWKS endpoint's 5-minute response cache,
/// so its idempotency and exact key target are pinned here.
/// </summary>
public class MemoryCacheJwksCacheInvalidatorTests
{
    [Fact]
    public void Invalidate_removes_the_jwks_document_key()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set(MemoryCacheJwksCacheInvalidator.CacheKey, "stale-document");

        var sut = new MemoryCacheJwksCacheInvalidator(cache);

        sut.Invalidate();

        cache.TryGetValue(MemoryCacheJwksCacheInvalidator.CacheKey, out _).Should().BeFalse();
    }

    [Fact]
    public void Invalidate_is_idempotent_when_key_missing()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = new MemoryCacheJwksCacheInvalidator(cache);

        System.Action act = () => sut.Invalidate();

        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_throws_when_cache_is_null()
    {
        System.Action act = () => new MemoryCacheJwksCacheInvalidator(null!);

        act.Should().Throw<System.ArgumentNullException>();
    }
}
