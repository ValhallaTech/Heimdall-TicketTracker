using System;
using System.Threading;
using System.Threading.Tasks;

namespace Heimdall.Core.Interfaces;

/// <summary>
/// Abstraction over a distributed cache (e.g. Redis) used by the BLL for read-through caching.
/// </summary>
public interface ICacheService
{
    /// <summary>Returns the cached value for <paramref name="key"/> or <c>null</c> if absent/unreachable.</summary>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class;

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/> with the given TTL.</summary>
    Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default
    )
        where T : class;

    /// <summary>Removes the cached value under <paramref name="key"/>, if any.</summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}
