using System;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using StackExchange.Redis;

namespace Heimdall.DAL.Caching;

/// <summary>
/// StackExchange.Redis-backed implementation of <see cref="ICacheService"/>.
/// Cache misses and transient failures degrade gracefully (return null / swallow).
/// </summary>
public class RedisCacheService : ICacheService
{
    // Newtonsoft.Json with default settings + camelCase resolver is what Midgard uses
    // for the same key namespace, so payloads are interoperable. The default handling for
    // enums is integer serialization, which round-trips losslessly for TicketStatus /
    // TicketPriority (both int-backed). If a future contract change switches enums to
    // string form via StringEnumConverter, BOTH the producer and consumer settings must be
    // updated in lockstep — otherwise reads will throw JsonReaderException.
    private static readonly JsonSerializerSettings SerializerSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
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
        try
        {
            var value = await GetDatabase().StringGetAsync(key).ConfigureAwait(false);
            if (value.IsNullOrEmpty)
            {
                return null;
            }

            return JsonConvert.DeserializeObject<T>((string)value!, SerializerSettings);
        }
        catch (Exception ex) when (ex is RedisException or Newtonsoft.Json.JsonException)
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
        try
        {
            var payload = JsonConvert.SerializeObject(value, SerializerSettings);
            await GetDatabase()
                .StringSetAsync(key, payload, ttl, When.Always)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or Newtonsoft.Json.JsonException)
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
        try
        {
            await GetDatabase().KeyDeleteAsync(key).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Redis DEL failed for key {CacheKey}.", key);
        }
    }
}
