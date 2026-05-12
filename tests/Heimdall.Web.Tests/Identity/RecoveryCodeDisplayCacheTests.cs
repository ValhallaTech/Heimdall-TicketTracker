using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Heimdall.Web.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Heimdall.Web.Tests.Identity;

/// <summary>
/// Unit tests for <see cref="RecoveryCodeDisplayCache"/>. The cache is a
/// one-shot, user-scoped, opaque-token-gated relay for freshly-generated
/// recovery codes (Phase 4.3 step 10). The invariants under test:
/// <list type="bullet">
///   <item><description><see cref="RecoveryCodeDisplayCache.Stash"/> mints a fresh non-empty <see cref="Guid"/> per call.</description></item>
///   <item><description><see cref="RecoveryCodeDisplayCache.Consume"/> returns the original list exactly once, then <c>null</c>.</description></item>
///   <item><description>A consume with a mismatched user id never reveals another user's codes.</description></item>
///   <item><description>The defensive copy means the caller mutating its own list cannot mutate the cached value.</description></item>
/// </list>
/// </summary>
public class RecoveryCodeDisplayCacheTests
{
    private static RecoveryCodeDisplayCache CreateCache()
    {
        // SizeLimit must be set because Stash sets entry Size=1.
        var memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions { SizeLimit = 1024 }));
        return new RecoveryCodeDisplayCache(memoryCache);
    }

    [Fact]
    public void Should_ReturnNonEmptyGuid_When_Stashing()
    {
        var cache = CreateCache();
        var userId = Guid.NewGuid();
        var codes = new[] { "alpha", "bravo" };

        var token = cache.Stash(userId, codes);

        token.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void Should_MintDistinctTokens_When_StashedRepeatedly()
    {
        var cache = CreateCache();
        var userId = Guid.NewGuid();

        var t1 = cache.Stash(userId, new[] { "a" });
        var t2 = cache.Stash(userId, new[] { "b" });

        t1.Should().NotBe(t2, "each stash must mint a fresh single-use token");
    }

    [Fact]
    public void Should_ReturnOriginalCodes_When_ConsumedOnce()
    {
        var cache = CreateCache();
        var userId = Guid.NewGuid();
        var codes = new[] { "code-1", "code-2", "code-3" };
        var token = cache.Stash(userId, codes);

        var consumed = cache.Consume(userId, token);

        consumed.Should().NotBeNull();
        consumed.Should().BeEquivalentTo(codes);
    }

    [Fact]
    public void Should_ReturnNull_When_ConsumedTwice()
    {
        // Atomic remove-on-read: the second consume must miss.
        var cache = CreateCache();
        var userId = Guid.NewGuid();
        var token = cache.Stash(userId, new[] { "code-1" });

        var first = cache.Consume(userId, token);
        var second = cache.Consume(userId, token);

        first.Should().NotBeNull();
        second.Should().BeNull("Consume must atomically remove the entry on first read");
    }

    [Fact]
    public void Should_ReturnNull_When_ConsumedWithWrongUserId()
    {
        // The cache key is (userId, token); a stolen token from a different
        // session must not replay another user's codes.
        var cache = CreateCache();
        var ownerId = Guid.NewGuid();
        var attackerId = Guid.NewGuid();
        var token = cache.Stash(ownerId, new[] { "secret" });

        var consumed = cache.Consume(attackerId, token);

        consumed.Should().BeNull("the cache key is user-scoped to prevent cross-user replay");
    }

    [Fact]
    public void Should_ReturnNull_When_TokenIsUnknown()
    {
        var cache = CreateCache();

        var consumed = cache.Consume(Guid.NewGuid(), Guid.NewGuid());

        consumed.Should().BeNull();
    }

    [Fact]
    public void Should_DefensivelyCopyCodes_When_StashedListIsMutated()
    {
        var cache = CreateCache();
        var userId = Guid.NewGuid();
        var mutable = new List<string> { "original-1", "original-2" };
        var token = cache.Stash(userId, mutable);

        // The caller mutates its own reference after stashing.
        mutable[0] = "tampered";
        mutable.Clear();

        var consumed = cache.Consume(userId, token);

        consumed.Should().NotBeNull();
        consumed.Should().BeEquivalentTo(new[] { "original-1", "original-2" });
    }

    [Fact]
    public void Should_Throw_When_StashingNullCodes()
    {
        var cache = CreateCache();

        Action act = () => cache.Stash(Guid.NewGuid(), null!);

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("codes");
    }

    [Fact]
    public void Should_AcceptEmptyCodes_When_Stashing()
    {
        // The production contract does not reject an empty list — defence in
        // depth in HandleMfaSetupVerifyAsync passes Array.Empty<string>() when
        // the recovery-code store returns null. The display cache must round-
        // trip that empty payload faithfully.
        var cache = CreateCache();
        var userId = Guid.NewGuid();

        var token = cache.Stash(userId, Array.Empty<string>());
        var consumed = cache.Consume(userId, token);

        consumed.Should().NotBeNull();
        consumed!.Should().BeEmpty();
    }

    [Fact]
    public void Should_Throw_When_ConstructedWithNullCache()
    {
        Action act = () => new RecoveryCodeDisplayCache(null!);

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("cache");
    }

    [Fact]
    public void Should_AllowExactlyOneCallerToObserveCodes_When_ConsumedConcurrently()
    {
        // Comment from PR review #45: the previous TryGetValue+Remove pair was
        // not atomic — two parallel Consume calls could both observe the same
        // codes before either Remove had taken effect. The current
        // Interlocked-backed SingleShotCodes wrapper makes the call race-free.
        const int parallelism = 32;
        var cache = CreateCache();
        var userId = Guid.NewGuid();
        var token = cache.Stash(userId, new[] { "a", "b", "c" });

        using var barrier = new System.Threading.Barrier(parallelism);
        var results = new IReadOnlyList<string>?[parallelism];

        System.Threading.Tasks.Parallel.For(0, parallelism, i =>
        {
            barrier.SignalAndWait();
            results[i] = cache.Consume(userId, token);
        });

        results.Count(r => r is not null).Should().Be(1,
            "Interlocked.Exchange must guarantee exactly one observer of the codes across concurrent racers");
    }

    // Expiry is enforced by IMemoryCache; covered by integration tests.
}
