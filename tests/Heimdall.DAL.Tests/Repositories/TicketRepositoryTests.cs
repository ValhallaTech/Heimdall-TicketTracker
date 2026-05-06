using Dapper;
using FluentAssertions;
using Heimdall.Core.Models;
using Heimdall.Core.Models.Pagination;
using Heimdall.DAL.Configuration;
using Heimdall.DAL.Repositories;
using Heimdall.DAL.Tests.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Heimdall.DAL.Tests.Repositories;

/// <summary>
/// Integration tests for <see cref="TicketRepository"/> after Phase 2.5 replaced the
/// legacy <c>reporter</c>/<c>assignee</c> string columns with FK Guid columns
/// (<c>project_id</c>, <c>team_id</c>, <c>reporter_id</c>, <c>assignee_id</c>).
/// Each test resets the tickets and collaboration tables, then seeds a minimum
/// org → team → project → reporter graph so ticket inserts satisfy
/// <c>ON DELETE RESTRICT</c> FKs.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class TicketRepositoryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private readonly TicketRepository _repo;
    private readonly OrganizationRepository _orgRepo;
    private readonly TeamRepository _teamRepo;
    private readonly ProjectRepository _projectRepo;

    private Guid _projectId;
    private Guid _teamId;
    private Guid _reporterId;
    private Guid _assigneeId;

    public TicketRepositoryTests(PostgresFixture fx)
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
        var team = new Team { OrganizationId = org.Id, Slug = "team", Name = "Team", CreatedBy = _reporterId };
        await _teamRepo.CreateAsync(team);
        _teamId = team.Id;
        var project = new Project { TeamId = _teamId, Slug = "proj", Name = "Proj", CreatedBy = _reporterId };
        await _projectRepo.CreateAsync(project);
        _projectId = project.Id;
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

    private Ticket Sample(
        string title = "T",
        Guid? assigneeId = null,
        TicketStatus s = TicketStatus.Open,
        TicketPriority p = TicketPriority.Medium)
    {
        var now = DateTimeOffset.UtcNow;
        return new Ticket
        {
            Title = title,
            Description = $"desc {title}",
            Status = s,
            Priority = p,
            ProjectId = _projectId,
            TeamId = _teamId,
            ReporterId = _reporterId,
            AssigneeId = assigneeId,
            DateCreated = now,
            DateUpdated = now,
        };
    }

    [Fact]
    public void Should_Throw_When_OptionsIsNull()
    {
        Action act = () => new TicketRepository(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_DapperIsNull()
    {
        Action act = () => new TicketRepository(
            Options.Create(new DataOptions { PostgresConnectionString = "x" }),
            null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_AssignIdAndPersist_When_CreateAsyncCalled()
    {
        var t = Sample("created");
        var id = await _repo.CreateAsync(t);
        id.Should().BeGreaterThan(0);
        t.Id.Should().Be(id);

        var fetched = await _repo.GetByIdAsync(id);
        fetched.Should().NotBeNull();
        fetched!.Title.Should().Be("created");
        fetched.Description.Should().Be("desc created");
        fetched.ProjectId.Should().Be(_projectId);
        fetched.TeamId.Should().Be(_teamId);
        fetched.ReporterId.Should().Be(_reporterId);
        fetched.AssigneeId.Should().BeNull();
    }

    [Fact]
    public async Task Should_PersistAssigneeId_When_CreatedWithAssignee()
    {
        var id = await _repo.CreateAsync(Sample("with-assignee", assigneeId: _assigneeId));

        var fetched = await _repo.GetByIdAsync(id);
        fetched!.AssigneeId.Should().Be(_assigneeId);
    }

    [Fact]
    public async Task Should_Throw_When_CreateAsyncTicketIsNull()
    {
        Func<Task> act = () => _repo.CreateAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_ReturnNull_When_GetByIdAsyncMisses()
    {
        var result = await _repo.GetByIdAsync(99999);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Should_ReturnAllRowsCappedAndSortedByDateDesc_When_GetAllAsyncCalled()
    {
        for (var i = 0; i < 3; i++)
        {
            await _repo.CreateAsync(Sample($"t{i}"));
        }

        var rows = await _repo.GetAllAsync();
        rows.Should().HaveCount(3);
        rows[0].DateCreated.Should().BeOnOrAfter(rows[^1].DateCreated);
    }

    [Fact]
    public async Task Should_ReturnPagedResults_When_GetPagedAsyncCalledWithoutDapper()
    {
        for (var i = 0; i < 5; i++)
        {
            await _repo.CreateAsync(Sample($"row {i}"));
        }

        var query = new PagedQuery(page: 1, pageSize: 2, searchText: null,
            sortField: "Title", sortDirection: SortDirection.Ascending);
        var (items, total) = await _repo.GetPagedAsync(query);

        total.Should().Be(5);
        items.Should().HaveCount(2);
        items.Select(t => t.Title).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Should_FilterBySearchText_When_GetPagedAsyncReceivesQuery()
    {
        await _repo.CreateAsync(Sample("apple"));
        await _repo.CreateAsync(Sample("banana"));
        await _repo.CreateAsync(Sample("apricot"));

        var (items, total) = await _repo.GetPagedAsync(
            new PagedQuery(1, 50, "ap", "Title", SortDirection.Ascending));

        total.Should().Be(2);
        items.Select(t => t.Title).Should().BeEquivalentTo(new[] { "apple", "apricot" });
    }

    [Fact]
    public async Task Should_FallBackToDateCreatedSort_When_SortFieldNotInAllowList()
    {
        await _repo.CreateAsync(Sample("a"));
        await _repo.CreateAsync(Sample("b"));

        var ctor = typeof(PagedQuery).GetConstructor(new[]
        {
            typeof(int), typeof(int), typeof(string), typeof(string), typeof(SortDirection),
        })!;
        var query = (PagedQuery)ctor.Invoke(new object?[] { 1, 50, null, "DROP-ME", SortDirection.Descending });

        var (items, _) = await _repo.GetPagedAsync(query);
        items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Should_Throw_When_GetPagedAsyncQueryIsNull()
    {
        Func<Task> act = () => _repo.GetPagedAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_UpdateAndReturnTrue_When_RowExists()
    {
        var t = Sample("orig");
        var id = await _repo.CreateAsync(t);

        t.Title = "updated";
        t.Description = "new desc";
        t.Status = TicketStatus.Resolved;
        t.Priority = TicketPriority.High;
        t.AssigneeId = _assigneeId;

        var ok = await _repo.UpdateAsync(t);

        ok.Should().BeTrue();
        var fetched = await _repo.GetByIdAsync(id);
        fetched!.Title.Should().Be("updated");
        fetched.Status.Should().Be(TicketStatus.Resolved);
        fetched.Priority.Should().Be(TicketPriority.High);
        fetched.AssigneeId.Should().Be(_assigneeId);
    }

    [Fact]
    public async Task Should_ReturnFalse_When_UpdateAsyncRowMissing()
    {
        var t = Sample("missing");
        t.Id = 999_999;
        var ok = await _repo.UpdateAsync(t);
        ok.Should().BeFalse();
    }

    [Fact]
    public async Task Should_Throw_When_UpdateAsyncTicketIsNull()
    {
        Func<Task> act = () => _repo.UpdateAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_DeleteAndReturnTrue_When_RowExists()
    {
        var id = await _repo.CreateAsync(Sample("delete-me"));

        var ok = await _repo.DeleteAsync(id);

        ok.Should().BeTrue();
        var fetched = await _repo.GetByIdAsync(id);
        fetched.Should().BeNull();
    }

    [Fact]
    public async Task Should_ReturnFalse_When_DeleteAsyncRowMissing()
    {
        var ok = await _repo.DeleteAsync(999_999);
        ok.Should().BeFalse();
    }

    // ---- Suite C — FK round-trip behaviors ----

    [Fact]
    public async Task Should_Throw_When_CreateAsyncWithUnknownProjectId()
    {
        var ticket = Sample("bad-fk");
        ticket.ProjectId = Guid.NewGuid();

        Func<Task> act = () => _repo.CreateAsync(ticket);

        await act.Should().ThrowAsync<PostgresException>()
            .Where(e => e.SqlState == "23503");
    }

    [Fact]
    public async Task Should_Throw_When_CreateAsyncWithUnknownTeamId()
    {
        var ticket = Sample("bad-team");
        ticket.TeamId = Guid.NewGuid();

        Func<Task> act = () => _repo.CreateAsync(ticket);

        await act.Should().ThrowAsync<PostgresException>()
            .Where(e => e.SqlState == "23503");
    }

    [Fact]
    public async Task Should_Throw_When_CreateAsyncWithUnknownReporterId()
    {
        var ticket = Sample("bad-reporter");
        ticket.ReporterId = Guid.NewGuid();

        Func<Task> act = () => _repo.CreateAsync(ticket);

        await act.Should().ThrowAsync<PostgresException>()
            .Where(e => e.SqlState == "23503");
    }

    [Fact]
    public async Task Should_Throw_When_DeleteProjectStillReferencedByTicket()
    {
        await _repo.CreateAsync(Sample("anchor"));

        Func<Task> act = async () =>
        {
            await using var conn = new NpgsqlConnection(_fx.ConnectionString);
            await conn.OpenAsync();
            await conn.ExecuteAsync("DELETE FROM projects WHERE id = @Id;", new { Id = _projectId });
        };

        await act.Should().ThrowAsync<PostgresException>()
            .Where(e => e.SqlState == "23001");
    }

    [Fact]
    public async Task Should_Throw_When_DeleteTeamStillReferencedByTicket()
    {
        await _repo.CreateAsync(Sample("anchor"));

        Func<Task> act = async () =>
        {
            await using var conn = new NpgsqlConnection(_fx.ConnectionString);
            await conn.OpenAsync();
            await conn.ExecuteAsync("DELETE FROM teams WHERE id = @Id;", new { Id = _teamId });
        };

        await act.Should().ThrowAsync<PostgresException>()
            .Where(e => e.SqlState == "23001");
    }

    [Fact]
    public async Task Should_Throw_When_DeleteReporterStillReferencedByTicket()
    {
        await _repo.CreateAsync(Sample("anchor"));

        Func<Task> act = async () =>
        {
            await using var conn = new NpgsqlConnection(_fx.ConnectionString);
            await conn.OpenAsync();
            await conn.ExecuteAsync("DELETE FROM users WHERE id = @Id;", new { Id = _reporterId });
        };

        await act.Should().ThrowAsync<PostgresException>()
            .Where(e => e.SqlState == "23001");
    }

    [Fact]
    public async Task Should_NullAssigneeId_When_AssigneeUserDeleted()
    {
        var id = await _repo.CreateAsync(Sample("with-assignee", assigneeId: _assigneeId));

        await using (var conn = new NpgsqlConnection(_fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("DELETE FROM users WHERE id = @Id;", new { Id = _assigneeId });
        }

        var fetched = await _repo.GetByIdAsync(id);
        fetched.Should().NotBeNull();
        fetched!.AssigneeId.Should().BeNull();
    }

    [Fact]
    public async Task Should_HaveTeamIdIndex_When_SchemaQueried()
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        var count = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM pg_indexes WHERE tablename = 'tickets' AND indexname = 'ix_tickets_team_id';");
        count.Should().Be(1);
    }

    [Fact]
    public async Task Should_HaveDroppedLegacyReporterAndAssigneeColumns_When_SchemaQueried()
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        var legacy = await conn.QueryAsync<string>(
            @"SELECT column_name FROM information_schema.columns
              WHERE table_name = 'tickets' AND column_name IN ('reporter', 'assignee');");
        legacy.Should().BeEmpty();
    }
}
