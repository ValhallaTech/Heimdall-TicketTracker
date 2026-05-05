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
/// Integration tests for <see cref="ProjectRepository"/>. Each test runs against a
/// real Postgres container; collaboration-hierarchy tables reset before each test.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ProjectRepositoryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private readonly ProjectRepository _repo;
    private readonly TeamRepository _teamRepo;
    private readonly OrganizationRepository _orgRepo;

    public ProjectRepositoryTests(PostgresFixture fx)
    {
        _fx = fx;
        var options = Options.Create(new DataOptions { PostgresConnectionString = fx.ConnectionString });
        _repo = new ProjectRepository(options);
        _teamRepo = new TeamRepository(options);
        _orgRepo = new OrganizationRepository(options);
    }

    public Task InitializeAsync() => _fx.ResetCollaborationTablesAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Guid> SeedUserAsync(string email = "owner@example.com")
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        return await conn.QuerySingleAsync<Guid>(@"
INSERT INTO users (email, normalized_email, security_stamp, concurrency_stamp, created_at, updated_at)
VALUES (@Email, @NormalizedEmail, 's', 'c', now(), now())
RETURNING id;",
            new { Email = email, NormalizedEmail = email.ToUpperInvariant() });
    }

    private async Task<(Guid TeamId, Guid OwnerId)> SeedTeamAsync(string teamSlug = "alpha")
    {
        var ownerId = await SeedUserAsync();
        var org = new Organization { Slug = "org", Name = "Org", CreatedBy = ownerId };
        await _orgRepo.CreateAsync(org);
        var team = new Team { OrganizationId = org.Id, Slug = teamSlug, Name = teamSlug, CreatedBy = ownerId };
        await _teamRepo.CreateAsync(team);
        return (team.Id, ownerId);
    }

    [Fact]
    public void Should_Throw_When_OptionsIsNull()
    {
        Action act = () => new ProjectRepository(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task CreateAsync_Should_PopulateIdAndCreatedAt()
    {
        var (teamId, ownerId) = await SeedTeamAsync();
        var project = new Project { TeamId = teamId, Slug = "backend", Name = "Backend", CreatedBy = ownerId };

        var id = await _repo.CreateAsync(project);

        id.Should().NotBe(Guid.Empty);
        project.Id.Should().Be(id);
        project.CreatedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task GetByIdAsync_Should_ReturnPersistedRow()
    {
        var (teamId, ownerId) = await SeedTeamAsync();
        var project = new Project { TeamId = teamId, Slug = "backend", Name = "Backend", CreatedBy = ownerId };
        await _repo.CreateAsync(project);

        var fetched = await _repo.GetByIdAsync(project.Id);

        fetched.Should().NotBeNull();
        fetched!.TeamId.Should().Be(teamId);
        fetched.Slug.Should().Be("backend");
    }

    [Fact]
    public async Task GetByIdAsync_Should_ReturnNull_When_NotFound()
    {
        (await _repo.GetByIdAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task GetBySlugAsync_Should_BeScopedPerTeam_AndCaseInsensitive()
    {
        var ownerId = await SeedUserAsync();
        var org = new Organization { Slug = "org", Name = "Org", CreatedBy = ownerId };
        await _orgRepo.CreateAsync(org);
        var teamA = new Team { OrganizationId = org.Id, Slug = "alpha", Name = "Alpha", CreatedBy = ownerId };
        var teamB = new Team { OrganizationId = org.Id, Slug = "beta", Name = "Beta", CreatedBy = ownerId };
        await _teamRepo.CreateAsync(teamA);
        await _teamRepo.CreateAsync(teamB);

        await _repo.CreateAsync(new Project { TeamId = teamA.Id, Slug = "Backend", Name = "A", CreatedBy = ownerId });
        await _repo.CreateAsync(new Project { TeamId = teamB.Id, Slug = "backend", Name = "B", CreatedBy = ownerId });

        var fromA = await _repo.GetBySlugAsync(teamA.Id, "BACKEND");
        var fromB = await _repo.GetBySlugAsync(teamB.Id, "backend");

        fromA.Should().NotBeNull();
        fromB.Should().NotBeNull();
        fromA!.Id.Should().NotBe(fromB!.Id);
    }

    [Fact]
    public async Task GetBySlugAsync_Should_Throw_When_SlugBlank()
    {
        Func<Task> act = () => _repo.GetBySlugAsync(Guid.NewGuid(), " ");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetByTeamAsync_Should_ReturnSortedBySlug()
    {
        var (teamId, ownerId) = await SeedTeamAsync();
        await _repo.CreateAsync(new Project { TeamId = teamId, Slug = "zeta", Name = "Z", CreatedBy = ownerId });
        await _repo.CreateAsync(new Project { TeamId = teamId, Slug = "alpha", Name = "A", CreatedBy = ownerId });

        var rows = await _repo.GetByTeamAsync(teamId);

        rows.Select(r => r.Slug).Should().ContainInOrder("alpha", "zeta");
    }

    [Fact]
    public async Task UpdateAsync_Should_MutateSlugAndName_NotTeamId()
    {
        var (teamId, ownerId) = await SeedTeamAsync();
        var org = (await _orgRepo.GetAllAsync()).Single();
        var otherTeam = new Team { OrganizationId = org.Id, Slug = "other", Name = "Other", CreatedBy = ownerId };
        await _teamRepo.CreateAsync(otherTeam);

        var project = new Project { TeamId = teamId, Slug = "old", Name = "Old", CreatedBy = ownerId };
        await _repo.CreateAsync(project);

        project.Slug = "new";
        project.Name = "New";
        project.TeamId = otherTeam.Id;
        await _repo.UpdateAsync(project);

        var fetched = await _repo.GetByIdAsync(project.Id);
        fetched!.Slug.Should().Be("new");
        fetched.Name.Should().Be("New");
        fetched.TeamId.Should().Be(teamId, "team_id is immutable on update");
    }

    [Fact]
    public async Task UpdateAsync_Should_ReturnFalse_When_NotFound()
    {
        var ghost = new Project
        {
            Id = Guid.NewGuid(),
            TeamId = Guid.NewGuid(),
            Slug = "g",
            Name = "G",
            CreatedBy = Guid.NewGuid(),
        };

        (await _repo.UpdateAsync(ghost)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_Should_RemoveRow()
    {
        var (teamId, ownerId) = await SeedTeamAsync();
        var project = new Project { TeamId = teamId, Slug = "doomed", Name = "Doomed", CreatedBy = ownerId };
        await _repo.CreateAsync(project);

        (await _repo.DeleteAsync(project.Id)).Should().BeTrue();
        (await _repo.GetByIdAsync(project.Id)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_Should_ReturnFalse_When_NotFound()
    {
        (await _repo.DeleteAsync(Guid.NewGuid())).Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_Should_RejectDuplicateSlug_WithinTeam()
    {
        var (teamId, ownerId) = await SeedTeamAsync();
        await _repo.CreateAsync(new Project { TeamId = teamId, Slug = "Backend", Name = "B", CreatedBy = ownerId });

        Func<Task> act = () => _repo.CreateAsync(new Project { TeamId = teamId, Slug = "backend", Name = "dup", CreatedBy = ownerId });
        await act.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "23505");
    }

    [Fact]
    public async Task CreateAsync_Should_Throw_When_ProjectIsNull()
    {
        Func<Task> act = () => _repo.CreateAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task UpdateAsync_Should_Throw_When_ProjectIsNull()
    {
        Func<Task> act = () => _repo.UpdateAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task DeletingTeam_Should_CascadeProjects()
    {
        var (teamId, ownerId) = await SeedTeamAsync();
        var project = new Project { TeamId = teamId, Slug = "child", Name = "Child", CreatedBy = ownerId };
        await _repo.CreateAsync(project);

        (await _teamRepo.DeleteAsync(teamId)).Should().BeTrue();
        (await _repo.GetByIdAsync(project.Id)).Should().BeNull();
    }
}
