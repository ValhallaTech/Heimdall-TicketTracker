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
/// Integration tests for <see cref="ProjectMemberRepository"/>. Each test runs
/// against a real Postgres container; collaboration-hierarchy and membership tables
/// reset before each test.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ProjectMemberRepositoryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private readonly ProjectMemberRepository _repo;
    private readonly ProjectRepository _projectRepo;
    private readonly TeamRepository _teamRepo;
    private readonly OrganizationRepository _orgRepo;

    public ProjectMemberRepositoryTests(PostgresFixture fx)
    {
        _fx = fx;
        var options = Options.Create(new DataOptions { PostgresConnectionString = fx.ConnectionString });
        _repo = new ProjectMemberRepository(options);
        _projectRepo = new ProjectRepository(options);
        _teamRepo = new TeamRepository(options);
        _orgRepo = new OrganizationRepository(options);
    }

    public Task InitializeAsync() => _fx.ResetCollaborationTablesAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Guid> SeedUserAsync(string email)
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        return await conn.QuerySingleAsync<Guid>(@"
INSERT INTO users (email, normalized_email, security_stamp, concurrency_stamp, created_at, updated_at)
VALUES (@Email, @NormalizedEmail, 's', 'c', now(), now())
RETURNING id;",
            new { Email = email, NormalizedEmail = email.ToUpperInvariant() });
    }

    private async Task<Guid> SeedProjectAsync(Guid ownerId, string projectSlug = "backend")
    {
        var existingOrg = (await _orgRepo.GetAllAsync()).FirstOrDefault();
        Guid orgId;
        if (existingOrg is null)
        {
            var org = new Organization { Slug = "org", Name = "Org", CreatedBy = ownerId };
            await _orgRepo.CreateAsync(org);
            orgId = org.Id;
        }
        else
        {
            orgId = existingOrg.Id;
        }

        var team = new Team { OrganizationId = orgId, Slug = $"team-{projectSlug}", Name = projectSlug, CreatedBy = ownerId };
        await _teamRepo.CreateAsync(team);

        var project = new Project { TeamId = team.Id, Slug = projectSlug, Name = projectSlug, CreatedBy = ownerId };
        await _projectRepo.CreateAsync(project);
        return project.Id;
    }

    [Fact]
    public void Should_Throw_When_OptionsIsNull()
    {
        Action act = () => new ProjectMemberRepository(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task AddAsync_Should_Throw_When_MemberIsNull()
    {
        Func<Task> act = () => _repo.AddAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task AddAsync_AndGetAsync_Should_RoundTripAllFields()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var projectId = await SeedProjectAsync(alice);

        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _repo.AddAsync(new ProjectMember
        {
            UserId = bob,
            ProjectId = projectId,
            Role = "member",
            AddedBy = alice,
        });
        var after = DateTimeOffset.UtcNow.AddMinutes(1);

        var fetched = await _repo.GetAsync(bob, projectId);

        fetched.Should().NotBeNull();
        fetched!.UserId.Should().Be(bob);
        fetched.ProjectId.Should().Be(projectId);
        fetched.Role.Should().Be("member");
        fetched.AddedBy.Should().Be(alice);
        fetched.AddedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public async Task GetAsync_Should_ReturnNull_When_NotFound()
    {
        (await _repo.GetAsync(Guid.NewGuid(), Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task AddAsync_Should_RejectDuplicateCompositeKey()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var projectId = await SeedProjectAsync(alice);

        await _repo.AddAsync(new ProjectMember { UserId = bob, ProjectId = projectId, Role = "member", AddedBy = alice });

        Func<Task> act = () => _repo.AddAsync(new ProjectMember { UserId = bob, ProjectId = projectId, Role = "admin", AddedBy = alice });
        await act.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "23505");
    }

    [Fact]
    public async Task GetByParentAsync_Should_ReturnOnlyMembersOfThatProject_OrderedByAddedAtThenUserId()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var carol = await SeedUserAsync("carol@example.com");
        var dave = await SeedUserAsync("dave@example.com");

        var project1 = await SeedProjectAsync(alice, "backend");
        var project2 = await SeedProjectAsync(alice, "frontend");

        await _repo.AddAsync(new ProjectMember { UserId = bob, ProjectId = project1, Role = "member", AddedBy = alice });
        await _repo.AddAsync(new ProjectMember { UserId = carol, ProjectId = project1, Role = "admin", AddedBy = alice });
        await _repo.AddAsync(new ProjectMember { UserId = dave, ProjectId = project1, Role = "viewer", AddedBy = alice });
        await _repo.AddAsync(new ProjectMember { UserId = bob, ProjectId = project2, Role = "owner", AddedBy = alice });

        var rows = await _repo.GetByParentAsync(project1);

        rows.Should().HaveCount(3);
        rows.Select(r => r.UserId).Should().BeEquivalentTo(new[] { bob, carol, dave });
        rows.Should().BeInAscendingOrder(r => r.AddedAt);
    }

    [Fact]
    public async Task GetByUserAsync_Should_ReturnOnlyThatUsersMemberships()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var carol = await SeedUserAsync("carol@example.com");

        var project1 = await SeedProjectAsync(alice, "backend");
        var project2 = await SeedProjectAsync(alice, "frontend");
        var project3 = await SeedProjectAsync(alice, "infra");

        await _repo.AddAsync(new ProjectMember { UserId = bob, ProjectId = project1, Role = "member", AddedBy = alice });
        await _repo.AddAsync(new ProjectMember { UserId = bob, ProjectId = project2, Role = "admin", AddedBy = alice });
        await _repo.AddAsync(new ProjectMember { UserId = carol, ProjectId = project3, Role = "viewer", AddedBy = alice });

        var rows = await _repo.GetByUserAsync(bob);

        rows.Should().HaveCount(2);
        rows.Select(r => r.ProjectId).Should().BeEquivalentTo(new[] { project1, project2 });
        rows.Should().BeInAscendingOrder(r => r.AddedAt);
    }

    [Fact]
    public async Task UpdateRoleAsync_Should_ChangeRole_AndReturnTrue()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var projectId = await SeedProjectAsync(alice);

        await _repo.AddAsync(new ProjectMember { UserId = bob, ProjectId = projectId, Role = "member", AddedBy = alice });

        (await _repo.UpdateRoleAsync(bob, projectId, "admin")).Should().BeTrue();

        (await _repo.GetAsync(bob, projectId))!.Role.Should().Be("admin");
    }

    [Fact]
    public async Task UpdateRoleAsync_Should_ReturnFalse_When_NotFound()
    {
        (await _repo.UpdateRoleAsync(Guid.NewGuid(), Guid.NewGuid(), "member")).Should().BeFalse();
    }

    [Fact]
    public async Task UpdateRoleAsync_Should_Throw_When_RoleBlank()
    {
        Func<Task> act = () => _repo.UpdateRoleAsync(Guid.NewGuid(), Guid.NewGuid(), " ");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    [InlineData("member")]
    [InlineData("viewer")]
    public async Task AddAsync_Should_AcceptEveryEnumWireValue(string role)
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var projectId = await SeedProjectAsync(alice);

        await _repo.AddAsync(new ProjectMember { UserId = bob, ProjectId = projectId, Role = role, AddedBy = alice });

        (await _repo.GetAsync(bob, projectId))!.Role.Should().Be(role);
    }

    [Fact]
    public async Task RemoveAsync_Should_DeleteRow_AndReturnTrue_OnlyOnce()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var projectId = await SeedProjectAsync(alice);

        await _repo.AddAsync(new ProjectMember { UserId = bob, ProjectId = projectId, Role = "member", AddedBy = alice });

        (await _repo.RemoveAsync(bob, projectId)).Should().BeTrue();
        (await _repo.GetAsync(bob, projectId)).Should().BeNull();
        (await _repo.RemoveAsync(bob, projectId)).Should().BeFalse();
    }

    [Fact]
    public async Task DeletingParentProject_Should_CascadeMemberships()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var projectId = await SeedProjectAsync(alice);

        await _repo.AddAsync(new ProjectMember { UserId = bob, ProjectId = projectId, Role = "member", AddedBy = alice });

        (await _projectRepo.DeleteAsync(projectId)).Should().BeTrue();
        (await _repo.GetAsync(bob, projectId)).Should().BeNull();
    }

    [Fact]
    public async Task DeletingMemberUser_Should_CascadeMemberships()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var projectId = await SeedProjectAsync(alice);

        await _repo.AddAsync(new ProjectMember { UserId = bob, ProjectId = projectId, Role = "member", AddedBy = alice });

        await using (var conn = new NpgsqlConnection(_fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("DELETE FROM users WHERE id = @Id", new { Id = bob });
        }

        (await _repo.GetAsync(bob, projectId)).Should().BeNull();
    }

    [Fact]
    public async Task DeletingInviterUser_Should_BeBlockedByRestrictFk()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var carol = await SeedUserAsync("carol@example.com");
        var projectId = await SeedProjectAsync(alice);

        // Carol is the inviter only — not org/team/project owner and not the
        // member — so the only FK her deletion can trip is
        // project_members.added_by.
        await _repo.AddAsync(new ProjectMember { UserId = bob, ProjectId = projectId, Role = "member", AddedBy = carol });

        Func<Task> act = async () =>
        {
            await using var conn = new NpgsqlConnection(_fx.ConnectionString);
            await conn.OpenAsync();
            await conn.ExecuteAsync("DELETE FROM users WHERE id = @Id", new { Id = carol });
        };

        // ON DELETE RESTRICT raises SQLSTATE 23503 (foreign_key_violation).
        await act.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "23001" || ex.SqlState == "23503");
    }
}
