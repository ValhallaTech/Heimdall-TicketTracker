using System;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Heimdall.BLL.Tokens;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace Heimdall.BLL.Tests.Tokens;

/// <summary>
/// Phase 5.5 step 11 — Testcontainers integration suite for
/// <see cref="RedisAccessTokenDenylist"/>. Mirrors the Phase 3 Testcontainers
/// pattern (a per-class container instance via <see cref="IAsyncLifetime"/>) so
/// each test class is fully isolated. The container image
/// (<c>redis:8-alpine</c>) tracks <c>docker-compose.yml</c>; Renovate Bot
/// manages the tag.
/// </summary>
/// <remarks>
/// Tagged <c>[Trait("Category", "Integration")]</c> per the test conventions —
/// these tests require Docker.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class RedisAccessTokenDenylistIntegrationTests : IAsyncLifetime
{
    private const string KeyPrefix = RedisAccessTokenDenylist.KeyPrefix;

    private readonly IContainer _container = new ContainerBuilder("redis:8-alpine")
        .WithPortBinding(6379, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "PING"))
        .Build();

    private ConnectionMultiplexer? _multiplexer;

    public async Task InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);

        ushort host = _container.GetMappedPublicPort(6379);
        var options = ConfigurationOptions.Parse($"localhost:{host}");
        options.AbortOnConnectFail = false;
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (_multiplexer is not null)
        {
            await _multiplexer.CloseAsync().ConfigureAwait(false);
            await _multiplexer.DisposeAsync().ConfigureAwait(false);
        }

        await _container.DisposeAsync().ConfigureAwait(false);
    }

    private RedisAccessTokenDenylist CreateSut() =>
        new(_multiplexer!, NullLogger<RedisAccessTokenDenylist>.Instance);

    [Fact]
    public async Task Should_WriteKeyWithReasonAndPositiveTtl_When_DenyAsyncCalled()
    {
        var sut = CreateSut();
        string jti = Guid.NewGuid().ToString("N");
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);

        await sut.DenyAsync(jti, expiresAt, "logout", CancellationToken.None);

        IDatabase db = _multiplexer!.GetDatabase();
        RedisValue value = await db.StringGetAsync(KeyPrefix + jti);
        value.HasValue.Should().BeTrue();
        value.ToString().Should().Be("logout");

        TimeSpan? ttl = await db.KeyTimeToLiveAsync(KeyPrefix + jti);
        ttl.Should().NotBeNull();
        TimeSpan ttlValue = ttl.Value;
        // Positive TTL within reasonable bounds of (exp - now) ≈ 10 minutes.
        ttlValue.Should().BeGreaterThan(TimeSpan.FromMinutes(9));
        ttlValue.Should().BeLessThan(TimeSpan.FromMinutes(11));
    }

    [Fact]
    public async Task Should_ReturnDeniedWithReason_When_JtiIsDenied()
    {
        var sut = CreateSut();
        string jti = Guid.NewGuid().ToString("N");
        await sut.DenyAsync(jti, DateTimeOffset.UtcNow.AddMinutes(5), "admin_revoke", CancellationToken.None);

        DenylistLookup result = await sut.IsDeniedAsync(jti, CancellationToken.None);

        result.Denied.Should().BeTrue();
        result.Reason.Should().Be("admin_revoke");
    }

    [Fact]
    public async Task Should_ReturnNotDeniedWithNullReason_When_JtiIsAbsent()
    {
        var sut = CreateSut();
        string jti = "never-denied-" + Guid.NewGuid().ToString("N");

        DenylistLookup result = await sut.IsDeniedAsync(jti, CancellationToken.None);

        result.Denied.Should().BeFalse();
        result.Reason.Should().BeNull();
    }

    [Fact]
    public async Task Should_UpdateTtl_When_DenyAsyncCalledTwiceWithLaterExpiry()
    {
        var sut = CreateSut();
        string jti = Guid.NewGuid().ToString("N");
        IDatabase db = _multiplexer!.GetDatabase();

        // First deny with short(ish) TTL ≈ 2 minutes.
        await sut.DenyAsync(jti, DateTimeOffset.UtcNow.AddMinutes(2), "logout", CancellationToken.None);
        TimeSpan? firstTtl = await db.KeyTimeToLiveAsync(KeyPrefix + jti);
        firstTtl.Should().NotBeNull();
        TimeSpan firstTtlValue = firstTtl.Value;

        // Second deny with a much later exp must extend the TTL.
        await sut.DenyAsync(jti, DateTimeOffset.UtcNow.AddMinutes(30), "admin_revoke", CancellationToken.None);
        TimeSpan? secondTtl = await db.KeyTimeToLiveAsync(KeyPrefix + jti);

        secondTtl.Should().NotBeNull();
        TimeSpan secondTtlValue = secondTtl.Value;
        secondTtlValue.Should().BeGreaterThan(firstTtlValue);
        secondTtlValue.Should().BeGreaterThan(TimeSpan.FromMinutes(25));

        // Reason updated to the later call's value.
        RedisValue value = await db.StringGetAsync(KeyPrefix + jti);
        value.ToString().Should().Be("admin_revoke");
    }

    [Fact]
    public async Task Should_ClampToMinimum30sTtl_When_ExpIsInThePast()
    {
        var sut = CreateSut();
        string jti = Guid.NewGuid().ToString("N");
        IDatabase db = _multiplexer!.GetDatabase();

        // exp already 5 minutes in the past — must clamp to ≥ 30s, but not
        // significantly more, so the key actually expires reasonably soon.
        await sut.DenyAsync(jti, DateTimeOffset.UtcNow.AddMinutes(-5), "logout", CancellationToken.None);

        TimeSpan? ttl = await db.KeyTimeToLiveAsync(KeyPrefix + jti);
        ttl.Should().NotBeNull();
        TimeSpan ttlValue = ttl.Value;
        ttlValue.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
        ttlValue.Should().BeGreaterThan(TimeSpan.FromSeconds(20),
            because: "TTL is clamped to the documented minimum of 30s");

        // Verify the key actually expires by polling Redis (no Thread.Sleep).
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        bool expired = false;
        while (DateTime.UtcNow < deadline)
        {
            RedisValue current = await db.StringGetAsync(KeyPrefix + jti);
            if (!current.HasValue)
            {
                expired = true;
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        expired.Should().BeTrue(
            because: "the clamped 30s TTL must have elapsed within the polling window");
    }

    [Fact]
    public async Task Should_ThrowOperationCanceled_When_DenyAsyncCalledWithCanceledToken()
    {
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        string jti = Guid.NewGuid().ToString("N");
        IDatabase db = _multiplexer!.GetDatabase();

        Func<Task> act = () => sut.DenyAsync(
            jti, DateTimeOffset.UtcNow.AddMinutes(5), "logout", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        // Confirm the cancellation pre-empted the Redis write entirely.
        RedisValue value = await db.StringGetAsync(KeyPrefix + jti);
        value.HasValue.Should().BeFalse(
            because: "a pre-cancelled token must short-circuit before touching Redis");
    }

    [Fact]
    public async Task Should_ThrowOperationCanceled_When_IsDeniedAsyncCalledWithCanceledToken()
    {
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => sut.IsDeniedAsync(Guid.NewGuid().ToString("N"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Should_ThrowArgumentException_When_DenyAsyncCalledWithWhitespaceJti()
    {
        var sut = CreateSut();

        Func<Task> act = () => sut.DenyAsync(
            "   ", DateTimeOffset.UtcNow.AddMinutes(5), "logout", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("jti");
    }

    [Fact]
    public async Task Should_ThrowArgumentException_When_DenyAsyncCalledWithWhitespaceReason()
    {
        var sut = CreateSut();

        Func<Task> act = () => sut.DenyAsync(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow.AddMinutes(5),
            "  ",
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("reason");
    }

    [Fact]
    public async Task Should_ThrowArgumentException_When_IsDeniedAsyncCalledWithWhitespaceJti()
    {
        var sut = CreateSut();

        Func<Task> act = () => sut.IsDeniedAsync(" ", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("jti");
    }

    [Fact]
    public void Should_ThrowArgumentNull_When_ConstructedWithNullMultiplexer()
    {
        Action act = () => _ = new RedisAccessTokenDenylist(
            null!, NullLogger<RedisAccessTokenDenylist>.Instance);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_ThrowArgumentNull_When_ConstructedWithNullLogger()
    {
        Action act = () => _ = new RedisAccessTokenDenylist(_multiplexer!, null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
