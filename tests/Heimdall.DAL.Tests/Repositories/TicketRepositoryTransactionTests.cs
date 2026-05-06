using Dapper;
using FluentAssertions;
using Heimdall.Core.Models;
using Heimdall.DAL.Configuration;
using Heimdall.DAL.Repositories;
using Heimdall.DAL.Tests.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Heimdall.DAL.Tests.Repositories;

/// <summary>
/// Integration tests for the transaction-aware overloads
/// <see cref="TicketRepository.UpdateTeamAsync"/> and
/// <see cref="TicketRepository.UpdateAssigneeAsync"/> introduced in Phase 2.7
/// (docs/proposals/team-collaboration.md §5.4). These overloads accept a caller-owned
/// connection + transaction so the ticket UPDATE can ride alongside the audit-event
/// INSERT in a single commit-or-rollback unit.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class TicketRepositoryTransactionTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private readonly TicketRepository _repo;
    private readonly OrganizationRepository _orgRepo;
    private readonly TeamRepository _teamRepo;
    private readonly ProjectRepository _projectRepo;

    private Guid _projectId;
    private Guid _originalTeamId;
    private Guid _otherTeamId;
    private Guid _reporterId;
    private Guid _assigneeId;
    private int _ticketId;

    public TicketRepositoryTransactionTests(PostgresFixture fx)
    {
        _fx = fx;
        var options = Options.Create(new DataOptions { PostgresConnectionString = fx.ConnectionString });
        _repo = new TicketRepository(options);
        _orgRepo = new OrganizationRepository(options);
        _teamRepo = new TeamRepository(options);
        _projectRepo = new ProjectRepository(options);
    }

    public async Task InitializeAsync()
    {
        await _fx.ResetTicketsAndCollaborationTablesAsync();
        _reporterId = await SeedUserAsync("reporter@example.com");
        _assigneeId = await SeedUserAsync("assignee@example.com");

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
        _projectId = project.Id;

        // Seed a single ticket on the original team with no assignee — the route /
        // assign tests will mutate exactly these two columns.
        var now = DateTimeOffset.UtcNow;
        var ticket = new Ticket
        {
            Title = "T",
            Description = "desc",
            Status = TicketStatus.Open,
            Priority = TicketPriority.Medium,
            ProjectId = _projectId,
            TeamId = _originalTeamId,
            ReporterId = _reporterId,
            AssigneeId = null,
            DateCreated = now,
            DateUpdated = now,
        };
        _ticketId = await _repo.CreateAsync(ticket);
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

    private async Task<Guid> ReadTeamIdAsync()
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        return await conn.QuerySingleAsync<Guid>(
            "SELECT team_id FROM tickets WHERE id = @Id",
            new { Id = _ticketId });
    }

    private async Task<Guid?> ReadAssigneeIdAsync()
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        return await conn.QuerySingleAsync<Guid?>(
            "SELECT assignee_id FROM tickets WHERE id = @Id",
            new { Id = _ticketId });
    }

    [Fact]
    public async Task UpdateTeamAsync_Should_PersistChange_When_TransactionIsCommitted()
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var rows = await _repo.UpdateTeamAsync(conn, tx, _ticketId, _otherTeamId);
        await tx.CommitAsync();

        rows.Should().BeTrue();
        (await ReadTeamIdAsync()).Should().Be(_otherTeamId);
    }

    [Fact]
    public async Task UpdateTeamAsync_Should_LeaveRowUnchanged_When_TransactionIsRolledBack()
    {
        await using (var conn = new NpgsqlConnection(_fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            var rows = await _repo.UpdateTeamAsync(conn, tx, _ticketId, _otherTeamId);
            rows.Should().BeTrue();

            await tx.RollbackAsync();
        }

        (await ReadTeamIdAsync()).Should().Be(_originalTeamId);
    }

    [Fact]
    public async Task UpdateTeamAsync_Should_ReturnFalse_When_TicketDoesNotExist()
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var rows = await _repo.UpdateTeamAsync(conn, tx, ticketId: 999_999, _otherTeamId);
        await tx.CommitAsync();

        rows.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAssigneeAsync_Should_PersistAssignment_When_TransactionIsCommitted()
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var rows = await _repo.UpdateAssigneeAsync(conn, tx, _ticketId, _assigneeId);
        await tx.CommitAsync();

        rows.Should().BeTrue();
        (await ReadAssigneeIdAsync()).Should().Be(_assigneeId);
    }

    [Fact]
    public async Task UpdateAssigneeAsync_Should_PersistUnassign_When_NewAssigneeIsNull()
    {
        // First, set an assignee outside the unit under test so we can observe the
        // null-assignment effect.
        await using (var setup = new NpgsqlConnection(_fx.ConnectionString))
        {
            await setup.OpenAsync();
            await setup.ExecuteAsync(
                "UPDATE tickets SET assignee_id = @A WHERE id = @Id",
                new { A = _assigneeId, Id = _ticketId });
        }

        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var rows = await _repo.UpdateAssigneeAsync(conn, tx, _ticketId, newAssigneeId: null);
        await tx.CommitAsync();

        rows.Should().BeTrue();
        (await ReadAssigneeIdAsync()).Should().BeNull();
    }

    [Fact]
    public async Task UpdateAssigneeAsync_Should_LeaveRowUnchanged_When_TransactionIsRolledBack()
    {
        await using (var conn = new NpgsqlConnection(_fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            var rows = await _repo.UpdateAssigneeAsync(conn, tx, _ticketId, _assigneeId);
            rows.Should().BeTrue();

            await tx.RollbackAsync();
        }

        (await ReadAssigneeIdAsync()).Should().BeNull();
    }

    [Fact]
    public async Task UpdateTeamAsync_Should_Throw_When_ConnectionIsNull()
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        Func<Task> act = () => _repo.UpdateTeamAsync(null!, tx, _ticketId, _otherTeamId);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task UpdateTeamAsync_Should_Throw_When_TransactionIsNull()
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();

        Func<Task> act = () => _repo.UpdateTeamAsync(conn, null!, _ticketId, _otherTeamId);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task UpdateAssigneeAsync_Should_Throw_When_ConnectionIsNull()
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        Func<Task> act = () => _repo.UpdateAssigneeAsync(null!, tx, _ticketId, _assigneeId);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task UpdateAssigneeAsync_Should_Throw_When_TransactionIsNull()
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();

        Func<Task> act = () => _repo.UpdateAssigneeAsync(conn, null!, _ticketId, _assigneeId);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
