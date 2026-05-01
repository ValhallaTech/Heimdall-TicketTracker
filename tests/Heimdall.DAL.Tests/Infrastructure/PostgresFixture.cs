using Dapper;
using Heimdall.DAL.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Heimdall.DAL.Tests.Infrastructure;

/// <summary>
/// Shared Postgres container fixture that boots a single container, runs the FluentMigrator
/// schema migrations, and exposes the resulting connection string for tests.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("heimdall_test")
        .WithUsername("heimdall")
        .WithPassword("heimdall")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Run FluentMigrator migrations to set up the schema. Also exercises
        // AddHeimdallMigrations / RunHeimdallMigrationsAsync (the runner extensions
        // namespace is covered by `*Migrations*` in coverlet.runsettings, but the
        // call-through is still useful for end-to-end coverage).
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddHeimdallMigrations(ConnectionString);
        await using var sp = services.BuildServiceProvider();
        await sp.RunHeimdallMigrationsAsync(maxAttempts: 1, retryDelay: TimeSpan.Zero);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>
    /// Wipes the <c>tickets</c> table and resets the identity sequence so each test
    /// starts from a clean, deterministic state.
    /// </summary>
    public async Task ResetTicketsTableAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("DELETE FROM tickets; ALTER SEQUENCE tickets_id_seq RESTART WITH 1;");
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "PostgresCollection";
}
