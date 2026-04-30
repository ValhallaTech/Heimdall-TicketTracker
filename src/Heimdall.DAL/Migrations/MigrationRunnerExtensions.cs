using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentMigrator.Runner;
using Heimdall.Core.Caching;
using Heimdall.Core.Interfaces;
using Heimdall.DAL.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Heimdall.DAL.Migrations;

/// <summary>
/// Extension helpers for wiring FluentMigrator into an application's service collection and
/// executing migrations at startup.
/// </summary>
public static class MigrationRunnerExtensions
{
    /// <summary>
    /// Registers FluentMigrator with the Postgres runner and scans this assembly for migrations.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="postgresConnectionString">Npgsql connection string to migrate against.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddHeimdallMigrations(
        this IServiceCollection services,
        string postgresConnectionString
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(postgresConnectionString);

        services
            .AddFluentMigratorCore()
            .ConfigureRunner(rb =>
                rb.AddPostgres()
                    .WithGlobalConnectionString(postgresConnectionString)
                    .ScanIn(Assembly.GetExecutingAssembly())
                    .For.Migrations()
            )
            .AddLogging(lb => lb.AddFluentMigratorConsole());

        return services;
    }

    /// <summary>
    /// Runs all pending migrations using a scope-bound <see cref="IMigrationRunner"/>.
    /// Retries up to <paramref name="maxAttempts"/> times with a fixed delay between attempts
    /// to handle transient connectivity failures that can occur when the database service is
    /// still starting up (e.g. first deploy on Render). Only transient
    /// <see cref="NpgsqlException"/> failures trigger a retry; non-transient exceptions
    /// (e.g. a broken migration script) propagate immediately.
    /// </summary>
    /// <param name="serviceProvider">Root service provider.</param>
    /// <param name="maxAttempts">Maximum number of attempts before re-throwing. Must be at least 1. Defaults to 5.</param>
    /// <param name="retryDelay">Delay between attempts. Must be non-negative. Defaults to 10 seconds.</param>
    /// <param name="cancellationToken">Token used to cancel the retry loop.</param>
    public static async Task RunHeimdallMigrationsAsync(
        this IServiceProvider serviceProvider,
        int maxAttempts = 5,
        TimeSpan? retryDelay = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        var delay = retryDelay ?? TimeSpan.FromSeconds(10);
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retryDelay),
                "Retry delay must be non-negative."
            );
        }

        var logger = serviceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Heimdall.DAL.Migrations");

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
                runner.MigrateUp();
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransientException(ex))
            {
                logger.LogWarning(
                    ex,
                    "Database migration failed on attempt {Attempt}/{MaxAttempts}. Retrying in {Delay}…",
                    attempt,
                    maxAttempts,
                    delay
                );

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Seeds the database with synthetic ticket data when <paramref name="seedRequested"/> is
    /// <see langword="true"/> and the <c>tickets</c> table is currently empty.
    /// If the table already contains at least one row the operation is skipped so that
    /// successive deployments remain idempotent.
    /// </summary>
    /// <param name="serviceProvider">Root service provider used to resolve logging services.</param>
    /// <param name="seedRequested">
    /// When <see langword="false"/> the method returns immediately without touching the database.
    /// Typically driven by an environment variable such as <c>SEED_DATABASE</c>.
    /// </param>
    /// <param name="postgresConnectionString">
    /// Npgsql connection string used for the idempotency check and passed to
    /// <see cref="DatabaseSeeder"/>.
    /// </param>
    /// <param name="count">
    /// Number of synthetic rows to insert when the table is empty. Must be at least 1.
    /// Defaults to <c>50</c>.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    public static async Task SeedIfRequestedAsync(
        this IServiceProvider serviceProvider,
        bool seedRequested,
        string postgresConnectionString,
        int count = 50,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(postgresConnectionString);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var logger = serviceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Heimdall.DAL.Seeding");

        if (!seedRequested)
        {
            logger.LogInformation("Database seeding skipped (SEED_DATABASE=false).");
            return;
        }

        await using var connection = new NpgsqlConnection(postgresConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var hasRows = await connection
            .ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    "SELECT EXISTS(SELECT 1 FROM tickets LIMIT 1)",
                    cancellationToken: cancellationToken
                )
            )
            .ConfigureAwait(false);

        if (hasRows)
        {
            logger.LogInformation("Database already contains tickets; skipping seed.");
            return;
        }

        logger.LogInformation("Seeding database with {Count} tickets\u2026", count);

        var seeder = new DatabaseSeeder(postgresConnectionString);
        await seeder.SeedAsync(count, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("Database seeding complete ({Count} tickets inserted).", count);

        // Invalidate any stale list cache that may have been written before seeding.
        var cache = serviceProvider.GetService<ICacheService>();
        if (cache is not null)
        {
            await cache
                .RemoveAsync(CacheKeys.TicketList, cancellationToken)
                .ConfigureAwait(false);
            logger.LogInformation("Redis list cache invalidated after seeding.");
        }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="ex"/> (or any exception in its inner-exception
    /// chain) is a transient <see cref="NpgsqlException"/> — i.e. one that is worth retrying,
    /// such as a connection-refused or network-timeout failure during startup.
    /// </summary>
    private static bool IsTransientException(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException { IsTransient: true })
            {
                return true;
            }
        }

        return false;
    }
}
