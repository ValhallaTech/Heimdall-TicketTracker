using Dapper;
using FluentAssertions;
using Heimdall.Core.Caching;
using Heimdall.Core.Interfaces;
using Heimdall.DAL.Migrations;
using Heimdall.DAL.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;

namespace Heimdall.DAL.Tests.Migrations;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class MigrationRunnerExtensionsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;

    public MigrationRunnerExtensionsTests(PostgresFixture fx) => _fx = fx;

    public Task InitializeAsync() => _fx.ResetTicketsTableAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Should_Throw_When_AddHeimdallMigrationsServicesIsNull()
    {
        Action act = () => MigrationRunnerExtensions.AddHeimdallMigrations(null!, "x");
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Should_Throw_When_AddHeimdallMigrationsConnectionStringIsBlank(string? cs)
    {
        Action act = () => new ServiceCollection().AddHeimdallMigrations(cs!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Should_Throw_When_RunHeimdallMigrationsServicesIsNull()
    {
        Func<Task> act = () => ((IServiceProvider)null!).RunHeimdallMigrationsAsync();
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_Throw_When_RunMaxAttemptsIsLessThanOne()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        Func<Task> act = () => sp.RunHeimdallMigrationsAsync(maxAttempts: 0);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Should_Throw_When_RunRetryDelayIsNegative()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        Func<Task> act = () => sp.RunHeimdallMigrationsAsync(retryDelay: TimeSpan.FromSeconds(-1));
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Should_Skip_When_SeedNotRequested()
    {
        var sp = BuildLoggingServiceProvider();
        Func<Task> act = () => sp.SeedIfRequestedAsync(seedRequested: false, _fx.ConnectionString);
        await act.Should().NotThrowAsync();

        // Ensure no rows were inserted.
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        var count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tickets");
        count.Should().Be(0);
    }

    [Fact]
    public async Task Should_Throw_When_SeedConnectionStringIsBlank()
    {
        var sp = BuildLoggingServiceProvider();
        Func<Task> act = () => sp.SeedIfRequestedAsync(true, "");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Should_Throw_When_SeedCountIsLessThanOne()
    {
        var sp = BuildLoggingServiceProvider();
        Func<Task> act = () => sp.SeedIfRequestedAsync(true, _fx.ConnectionString, count: 0);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Should_SeedFirstRunAndSkipSecond_When_TableEmpty()
    {
        var cache = new Mock<ICacheService>(MockBehavior.Loose);
        cache.Setup(c => c.RemoveAsync(CacheKeys.TicketList, It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);
        var sp = BuildLoggingServiceProvider(cache.Object);

        await sp.SeedIfRequestedAsync(true, _fx.ConnectionString, count: 3);

        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tickets")).Should().Be(3);

        // Second run should be a no-op (idempotent).
        await sp.SeedIfRequestedAsync(true, _fx.ConnectionString, count: 3);
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tickets")).Should().Be(3);

        cache.Verify(c => c.RemoveAsync(CacheKeys.TicketList, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static IServiceProvider BuildLoggingServiceProvider(ICacheService? cache = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        if (cache is not null)
        {
            services.AddSingleton(cache);
        }

        return services.BuildServiceProvider();
    }
}
