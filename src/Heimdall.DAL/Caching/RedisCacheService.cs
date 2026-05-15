using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Interfaces;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Heimdall.DAL.Caching;

/// <summary>
/// StackExchange.Redis-backed implementation of <see cref="ICacheService"/>.
/// Cache misses and transient failures degrade gracefully (return null / swallow).
/// </summary>
public class RedisCacheService : ICacheService
{
    // System.Text.Json with camelCase property naming. STJ is faster and lower-allocation
    // than Newtonsoft.Json (the previous serializer used here) and is the org-wide default
    // — see README. Default enum handling is integer serialization, which round-trips
    // losslessly for TicketStatus / TicketPriority (both int-backed). If a future contract
    // change switches enums to string form via JsonStringEnumConverter, BOTH the producer
    // and consumer settings must be updated in lockstep — otherwise reads will throw
    // JsonException.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly ILogger<RedisCacheService> _logger;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="multiplexer">Connected Redis multiplexer.</param>
    /// <param name="logger">Logger used for graceful-degradation diagnostics.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="multiplexer"/> or <paramref name="logger"/> is <see langword="null"/>.
    /// </exception>
    public RedisCacheService(IConnectionMultiplexer multiplexer, ILogger<RedisCacheService> logger)
    {
        ArgumentNullException.ThrowIfNull(multiplexer);
        ArgumentNullException.ThrowIfNull(logger);
        _multiplexer = multiplexer;
        _logger = logger;
    }

    private IDatabase GetDatabase() => _multiplexer.GetDatabase();

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // SE.Redis IDatabase.*Async methods do not accept a CancellationToken, so we
            // bridge the token via Task.WaitAsync. Combined with the pre-dispatch
            // ThrowIfCancellationRequested above, this propagates cancellation
            // end-to-end (both before dispatch and while the call is in flight) and
            // surfaces as OperationCanceledException, which the graceful-degradation
            // filter below intentionally does NOT catch.
            var value = await GetDatabase()
                .StringGetAsync(key)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (value.IsNullOrEmpty)
            {
                _logger.LogDebug("Redis GET miss for key {CacheKey}.", key);
                return null;
            }

            _logger.LogDebug("Redis GET hit for key {CacheKey}.", key);
            return JsonSerializer.Deserialize<T>((string)value!, SerializerOptions);
        }
        catch (Exception ex) when (ex is RedisException or JsonException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Redis GET failed for key {CacheKey}; returning null.", key);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var payload = JsonSerializer.Serialize(value, SerializerOptions);
            await GetDatabase()
                .StringSetAsync(key, payload, ttl, When.Always)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or JsonException or ObjectDisposedException)
        {
            _logger.LogWarning(
                ex,
                "Redis SET failed for key {CacheKey}; continuing without caching.",
                key
            );
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await GetDatabase()
                .KeyDeleteAsync(key)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Redis DEL failed for key {CacheKey}.", key);
        }
    }
}
