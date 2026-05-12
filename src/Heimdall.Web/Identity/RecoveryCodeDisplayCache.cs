using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Caching.Memory;

namespace Heimdall.Web.Identity;

/// <summary>
/// <see cref="IMemoryCache"/>-backed implementation of
/// <see cref="IRecoveryCodeDisplayCache"/>. Five-minute absolute expiration —
/// long enough for a user to follow the post-enrolment redirect, short enough
/// that an entry that escapes the consume path does not linger.
/// </summary>
/// <remarks>
/// <para>
/// Phase 4.3 step 10 (<c>docs/implementation/phase-4-checklist.md</c>). The
/// recovery codes are held only in this process's memory; they are not
/// distributed across replicas. That is intentional: the verify endpoint and
/// the redirected display page are guaranteed to hit the same replica because
/// the redirect is to a different URL on the same origin and the user's
/// browser carries the same affinity cookie. If the affinity layer were ever
/// to fail, the display page falls through to <see cref="Consume"/> returning
/// <c>null</c> and the page redirects home — a regenerated set is then
/// reachable from the regular MFA-management surface.
/// </para>
/// </remarks>
public sealed class RecoveryCodeDisplayCache : IRecoveryCodeDisplayCache
{
    private static readonly TimeSpan AbsoluteExpiration = TimeSpan.FromMinutes(5);

    private readonly IMemoryCache _cache;

    /// <summary>
    /// Initialises a new <see cref="RecoveryCodeDisplayCache"/>.
    /// </summary>
    /// <param name="cache">The backing memory cache.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="cache"/> is <c>null</c>.
    /// </exception>
    public RecoveryCodeDisplayCache(IMemoryCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    /// <inheritdoc />
    public Guid Stash(Guid userId, IReadOnlyList<string> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);

        Guid token = Guid.NewGuid();
        string key = BuildKey(userId, token);

        // Copy defensively so the caller mutating its own list cannot mutate
        // the entry we hand back to the display page.
        string[] copy = new string[codes.Count];
        for (int i = 0; i < codes.Count; i++)
        {
            copy[i] = codes[i];
        }

        MemoryCacheEntryOptions options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = AbsoluteExpiration,
            Size = 1,
        };
        // Wrap the codes in a single-shot holder so concurrent callers race on
        // an Interlocked.Exchange instead of the non-atomic TryGetValue/Remove
        // pair. Without the wrapper, two parallel Consume calls could both
        // observe the same value before either Remove had taken effect.
        _cache.Set(key, new SingleShotCodes(copy), options);
        return token;
    }

    /// <inheritdoc />
    public IReadOnlyList<string>? Consume(Guid userId, Guid token)
    {
        string key = BuildKey(userId, token);
        if (!_cache.TryGetValue(key, out SingleShotCodes? holder) || holder is null)
        {
            return null;
        }

        // Interlocked.Exchange guarantees exactly one caller observes the
        // non-null payload; subsequent racers see null. Only the winning
        // caller removes the cache entry so a late TryGetValue cannot resurrect
        // the holder for another reader.
        IReadOnlyList<string>? codes = holder.Take();
        if (codes is null)
        {
            return null;
        }

        _cache.Remove(key);
        return codes;
    }

    /// <summary>
    /// One-shot holder used to make <see cref="Consume"/> race-free across
    /// concurrent readers. The <see cref="Take"/> method uses
    /// <see cref="Interlocked.Exchange{T}(ref T, T)"/> so exactly one caller
    /// wins and receives the codes; all subsequent racers observe <c>null</c>.
    /// </summary>
    private sealed class SingleShotCodes
    {
        private IReadOnlyList<string>? _codes;

        public SingleShotCodes(IReadOnlyList<string> codes)
        {
            _codes = codes;
        }

        public IReadOnlyList<string>? Take()
            => Interlocked.Exchange(ref _codes, null);
    }

    private static string BuildKey(Guid userId, Guid token)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"mfa-recovery:{userId}:{token}");
}
