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
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
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

    /// <summary>
    /// Wipes the <c>users</c> and <c>audit_events</c> tables so each test starts from a
    /// clean, deterministic state. Phase 2.1+ added <c>ON DELETE RESTRICT</c> FKs from
    /// the collaboration hierarchy + membership tables back to <c>users</c>
    /// (e.g. <c>organizations.created_by</c>, <c>*_members.added_by</c>), so this method
    /// must also clear those tables first — otherwise tests sharing
    /// <see cref="PostgresCollection"/> with suites that create org/team/project rows
    /// will fail with <c>23001</c> when an earlier suite leaves orphan rows referencing
    /// users. The <c>audit_events.actor_user_id</c> FK is <c>ON DELETE SET NULL</c>, so
    /// its position in the sequence is not load-bearing. The Phase 4.1 MFA tables
    /// <c>user_recovery_codes</c> and <c>user_authenticator_keys</c> declare
    /// <c>ON DELETE CASCADE</c> FKs to <c>users</c>, so a bare <c>DELETE FROM users</c>
    /// would already drain them; they are listed explicitly first so the reset order is
    /// self-evident to readers of the test code.
    /// </summary>
    public async Task ResetUsersTableAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "DELETE FROM tickets; "
            + "DELETE FROM project_members; "
            + "DELETE FROM team_members; "
            + "DELETE FROM organization_members; "
            + "DELETE FROM projects; "
            + "DELETE FROM teams; "
            + "DELETE FROM organizations; "
            + "DELETE FROM user_recovery_codes; "
            + "DELETE FROM user_authenticator_keys; "
            + "DELETE FROM users; "
            + "DELETE FROM audit_events;"
        );
    }

    /// <summary>
    /// Wipes the Phase 2.1 collaboration-hierarchy tables (<c>projects</c>,
    /// <c>teams</c>, <c>organizations</c>) together with their Phase 2.2
    /// membership tables (<c>project_members</c>, <c>team_members</c>,
    /// <c>organization_members</c>), plus <c>users</c> and <c>audit_events</c>,
    /// so each integration test starts from a known-empty state. Order matters
    /// because the <c>created_by</c> / <c>added_by</c> FK chains use
    /// <c>ON DELETE RESTRICT</c> — leaf-first deletes (memberships before
    /// hierarchy parents, then <c>users</c>) avoid having to defer constraints.
    /// <c>audit_events</c> is also cleared so any rows written by earlier tests
    /// in the collection don't bleed into the next assertion;
    /// <c>audit_events.actor_user_id</c> is <c>ON DELETE SET NULL</c>, so its
    /// position in the sequence is not load-bearing. The Phase 4.1 MFA tables
    /// <c>user_recovery_codes</c> and <c>user_authenticator_keys</c> CASCADE on
    /// <c>users</c> deletion, but are listed explicitly so the order is obvious.
    /// </summary>
    public async Task ResetCollaborationTablesAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "DELETE FROM project_members; "
            + "DELETE FROM team_members; "
            + "DELETE FROM organization_members; "
            + "DELETE FROM projects; "
            + "DELETE FROM teams; "
            + "DELETE FROM organizations; "
            + "DELETE FROM user_recovery_codes; "
            + "DELETE FROM user_authenticator_keys; "
            + "DELETE FROM users; "
            + "DELETE FROM audit_events;"
        );
    }

    /// <summary>
    /// Wipes the <c>tickets</c> table together with the Phase 2.1 collaboration
    /// hierarchy and Phase 2.2 membership tables. Phase 2.5 added FK columns on
    /// <c>tickets</c> that reference <c>projects</c>, <c>teams</c>, and <c>users</c>
    /// with <c>ON DELETE RESTRICT</c> (assignee is <c>SET NULL</c>), so tickets must
    /// be cleared before any of those parents. The Phase 4.1 MFA tables
    /// <c>user_recovery_codes</c> and <c>user_authenticator_keys</c> CASCADE on
    /// <c>users</c> deletion, but are listed explicitly so the order is obvious.
    /// </summary>
    public async Task ResetTicketsAndCollaborationTablesAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "DELETE FROM tickets; "
            + "ALTER SEQUENCE tickets_id_seq RESTART WITH 1; "
            + "DELETE FROM project_members; "
            + "DELETE FROM team_members; "
            + "DELETE FROM organization_members; "
            + "DELETE FROM projects; "
            + "DELETE FROM teams; "
            + "DELETE FROM organizations; "
            + "DELETE FROM user_recovery_codes; "
            + "DELETE FROM user_authenticator_keys; "
            + "DELETE FROM users; "
            + "DELETE FROM audit_events;"
        );
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "PostgresCollection";
}
