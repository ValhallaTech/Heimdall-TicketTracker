using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Heimdall.BLL.Tokens;

/// <summary>
/// Phase 5.5 step 11 — Redis-backed <see cref="IAccessTokenDenylist"/> sitting on the
/// existing <see cref="IConnectionMultiplexer"/> sidecar registered in
/// <c>Program.cs</c> (the same connection that backs the Phase 3 cache). The
/// keyspace is <c>heimdall:token-denylist:&lt;jti&gt;</c>; values are short
/// machine-readable reason codes.
/// </summary>
/// <remarks>
/// <para>
/// Implementation choices that matter for correctness:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>StringSetAsync</c> is called <strong>without</strong>
///       <see cref="When.NotExists"/> so a second deny with a later <c>exp</c>
///       extends the TTL (matching the contract documented on
///       <see cref="IAccessTokenDenylist.DenyAsync"/>).
///     </description>
///   </item>
///   <item>
///     <description>
///       TTL is clamped to <see cref="MinDenyTtl"/> when <c>exp</c> is already in
///       the past — writing a zero-or-negative TTL would cause
///       <see cref="StackExchange.Redis"/> to throw, and a denylist entry that
///       expires before the next request is read serves no purpose; the minimum
///       window covers the validator's clock-skew tolerance.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="IsDeniedAsync"/> deliberately allows <see cref="RedisConnectionException"/>
///       and <see cref="RedisTimeoutException"/> to propagate — the JwtBearer
///       wire-up in <c>Program.cs</c> distinguishes a confirmed miss from a
///       transport failure to apply the Phase 5.5 step 12 outage policy.
///     </description>
///   </item>
/// </list>
/// </remarks>
public sealed class RedisAccessTokenDenylist : IAccessTokenDenylist
{
    /// <summary>Key prefix for every denylist entry.</summary>
    public const string KeyPrefix = "heimdall:token-denylist:";

    /// <summary>
    /// Lower bound on the entry TTL when <c>exp</c> has already elapsed. Matches the
    /// JwtBearer <c>ClockSkew</c> tolerance configured in <c>Program.cs</c>; a denylist
    /// entry shorter than this could be skipped by a clock-skewed validator.
    /// </summary>
    private static readonly TimeSpan MinDenyTtl = TimeSpan.FromSeconds(30);

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisAccessTokenDenylist> _logger;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="redis">The shared Redis multiplexer.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">Either argument is <c>null</c>.</exception>
    public RedisAccessTokenDenylist(
        IConnectionMultiplexer redis,
        ILogger<RedisAccessTokenDenylist> logger)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(logger);

        _redis = redis;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task DenyAsync(
        string jti,
        DateTimeOffset expiresAt,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jti))
        {
            throw new ArgumentException("jti must not be null or whitespace.", nameof(jti));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("reason must not be null or whitespace.", nameof(reason));
        }

        cancellationToken.ThrowIfCancellationRequested();

        TimeSpan ttl = expiresAt - DateTimeOffset.UtcNow;
        if (ttl < MinDenyTtl)
        {
            ttl = MinDenyTtl;
        }

        IDatabase database = _redis.GetDatabase();
        string key = BuildKey(jti);

        bool stored = await database
            .StringSetAsync(key, reason, ttl)
            .ConfigureAwait(false);

        if (!stored)
        {
            // StackExchange.Redis returns false only when the SET was rejected by an
            // explicit When flag — we don't pass one, so a non-true return here means
            // the server replied with something other than the OK we expect. Log and
            // swallow; the caller has already revoked the refresh family, so the worst
            // case is the next request must reach the (now-revoked) refresh path.
            _logger.LogWarning(
                "Redis SET for denylist entry {Key} returned non-true; entry may not be active.",
                key);
        }
    }

    /// <inheritdoc />
    public async Task<DenylistLookup> IsDeniedAsync(
        string jti,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jti))
        {
            throw new ArgumentException("jti must not be null or whitespace.", nameof(jti));
        }

        cancellationToken.ThrowIfCancellationRequested();

        IDatabase database = _redis.GetDatabase();
        string key = BuildKey(jti);

        RedisValue value = await database
            .StringGetAsync(key)
            .ConfigureAwait(false);

        if (!value.HasValue)
        {
            return new DenylistLookup(Denied: false, Reason: null);
        }

        return new DenylistLookup(Denied: true, Reason: value.ToString());
    }

    private static string BuildKey(string jti) =>
        string.Create(CultureInfo.InvariantCulture, $"{KeyPrefix}{jti}");
}
