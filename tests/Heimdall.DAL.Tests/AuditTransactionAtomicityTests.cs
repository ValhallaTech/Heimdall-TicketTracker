using Dapper;
using FluentAssertions;
using Heimdall.Core.Auditing;
using Heimdall.Core.Models;
using Heimdall.DAL.Auditing;
using Heimdall.DAL.Configuration;
using Heimdall.DAL.Repositories;
using Heimdall.DAL.Tests.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Heimdall.DAL.Tests;

/// <summary>
/// End-to-end atomicity coverage for the §5.4 transactional contract: the
/// <c>tickets</c> UPDATE issued by <see cref="TicketRepository.UpdateTeamAsync"/>
/// and the <c>audit_events</c> INSERT issued by
/// <see cref="AuditEventWriter.WriteAsync(System.Data.IDbConnection, System.Data.IDbTransaction, AuditEvent, System.Threading.CancellationToken)"/>
/// must commit or roll back as a single unit when issued on a caller-owned
/// connection + transaction. Component-level tests live alongside each repository;
/// this file is the cross-cutting "both succeed or both vanish" check.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AuditTransactionAtomicityTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private readonly TicketRepository _ticketRepo;
    private readonly AuditEventWriter _auditWriter;
    private readonly OrganizationRepository _orgRepo;
    private readonly TeamRepository _teamRepo;
    private readonly ProjectRepository _projectRepo;
    private readonly NpgsqlConnectionFactory _connectionFactory;

    private Guid _originalTeamId;
    private Guid _otherTeamId;
    private Guid _reporterId;
    private int _ticketId;

    public AuditTransactionAtomicityTests(PostgresFixture fx)
    {
        _fx = fx;
        var options = Options.Create(new DataOptions { PostgresConnectionString = fx.ConnectionString });
        _ticketRepo = new TicketRepository(options);
        _auditWriter = new AuditEventWriter(options);
        _orgRepo = new OrganizationRepository(options);
        _teamRepo = new TeamRepository(options);
        _projectRepo = new ProjectRepository(options);
        _connectionFactory = new NpgsqlConnectionFactory(options);
    }

    public async Task InitializeAsync()
    {
        await _fx.ResetTicketsAndCollaborationTablesAsync();
        _reporterId = await SeedUserAsync("reporter@example.com");

        var org = new Organization { Slug = "org", Name = "Org", CreatedBy = _reporterId };
        await _orgRepo.CreateAsync(org);

        var team1 = new Team { OrganizationId = org.Id, Slug = "team-a", Name = "Team A", CreatedBy = _reporterId };
        await _teamRepo.CreateAsync(team1);
        _originalTeamId = team1.Id;

        var team2 = new Team { OrganizationId = org.Id, Slug = "team-b", Name = "Team B", CreatedBy = _reporterId };
        await _teamRepo.CreateAsync(team2);
        _otherTeamId = team2.Id;

        var project = new Project { TeamId = _originalTeamId, Slug = "proj", Name = "Proj", CreatedBy = _reporterId };
        await _projectRepo.CreateAsync(project);

        var now = DateTimeOffset.UtcNow;
        var ticket = new Ticket
        {
            Title = "T",
            Description = "desc",
            Status = TicketStatus.Open,
            Priority = TicketPriority.Medium,
            ProjectId = project.Id,
            TeamId = _originalTeamId,
            ReporterId = _reporterId,
            AssigneeId = null,
            DateCreated = now,
            DateUpdated = now,
        };
        _ticketId = await _ticketRepo.CreateAsync(ticket);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Guid> SeedUserAsync(string email)
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        return await conn.QuerySingleAsync<Guid>(
            @"INSERT INTO users (email, normalized_email, security_stamp, concurrency_stamp, created_at, updated_at)
              VALUES (@Email, @NormalizedEmail, 's', 'c', now(), now())
              RETURNING id;",
            new { Email = email, NormalizedEmail = email.ToUpperInvariant() });
    }

    private async Task<Guid> ReadTeamIdAsync(int ticketId)
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        return await conn.QuerySingleAsync<Guid>(
            "SELECT team_id FROM tickets WHERE id = @Id",
            new { Id = ticketId });
    }

    private async Task<long> CountAuditEventsAsync(string eventType)
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM audit_events WHERE event_type = @EventType",
            new { EventType = eventType });
    }

    [Fact]
    public async Task Should_PersistBothMutations_When_TransactionCommits()
    {
        var auditEvent = new AuditEvent
        {
            ActorUserId = _reporterId,
            EventType = "ticket_routed.commit_test",
            Target = _ticketId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PayloadJson = "{\"from_team\":\"a\",\"to_team\":\"b\"}",
        };

        using (var connection = _connectionFactory.CreateConnection())
        {
            connection.Open();
            using var tx = connection.BeginTransaction();

            var updated = await _ticketRepo.UpdateTeamAsync(connection, tx, _ticketId, _otherTeamId);
            updated.Should().BeTrue();

            await _auditWriter.WriteAsync(connection, tx, auditEvent);
            tx.Commit();
        }

        (await ReadTeamIdAsync(_ticketId)).Should().Be(_otherTeamId);
        (await CountAuditEventsAsync("ticket_routed.commit_test")).Should().Be(1);
    }

    [Fact]
    public async Task Should_RollbackBothMutations_When_AuditWriteFails()
    {
        // Force the audit INSERT to throw by handing it an invalid Postgres inet literal
        // — the explicit `::inet` cast inside the writer's SQL will reject it and bubble
        // a Npgsql exception up to the caller, who then rolls back. The ticket UPDATE
        // that succeeded inside the same transaction must vanish along with it.
        var poison = new AuditEvent
        {
            ActorUserId = _reporterId,
            EventType = "ticket_routed.rollback_audit_test",
            Target = _ticketId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Ip = "not-a-valid-ip",
            PayloadJson = "{}",
        };

        var beforeCount = await CountAuditEventsAsync("ticket_routed.rollback_audit_test");

        using (var connection = _connectionFactory.CreateConnection())
        {
            connection.Open();
            using var tx = connection.BeginTransaction();

            try
            {
                var updated = await _ticketRepo.UpdateTeamAsync(connection, tx, _ticketId, _otherTeamId);
                updated.Should().BeTrue();

                await _auditWriter.WriteAsync(connection, tx, poison);
                tx.Commit();
                throw new Xunit.Sdk.XunitException("Expected the audit write to throw on the invalid inet literal.");
            }
            catch (Npgsql.PostgresException)
            {
                tx.Rollback();
            }
        }

        (await ReadTeamIdAsync(_ticketId)).Should().Be(_originalTeamId);
        (await CountAuditEventsAsync("ticket_routed.rollback_audit_test")).Should().Be(beforeCount);
    }

    [Fact]
    public async Task Should_LeaveBothMutationsAbsent_When_TicketUpdateMissesAndCallerRollsBack()
    {
        // Symmetric scenario: the UPDATE reports zero rows affected (ticket id doesn't
        // exist), the caller treats that as the row-vanished race documented in §5.4 and
        // rolls back. Even if an audit row had already been queued, none of it should
        // survive the rollback.
        var auditEvent = new AuditEvent
        {
            ActorUserId = _reporterId,
            EventType = "ticket_routed.rollback_update_test",
            Target = "999999",
            PayloadJson = "{}",
        };

        var beforeCount = await CountAuditEventsAsync("ticket_routed.rollback_update_test");

        using (var connection = _connectionFactory.CreateConnection())
        {
            connection.Open();
            using var tx = connection.BeginTransaction();

            // Intentionally write the audit row first so we can prove the rollback wipes it.
            await _auditWriter.WriteAsync(connection, tx, auditEvent);

            var updated = await _ticketRepo.UpdateTeamAsync(connection, tx, ticketId: 999_999, _otherTeamId);
            updated.Should().BeFalse();

            tx.Rollback();
        }

        (await ReadTeamIdAsync(_ticketId)).Should().Be(_originalTeamId);
        (await CountAuditEventsAsync("ticket_routed.rollback_update_test")).Should().Be(beforeCount);
    }
}
