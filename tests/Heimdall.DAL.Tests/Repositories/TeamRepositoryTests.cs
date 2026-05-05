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
/// Integration tests for <see cref="TeamRepository"/>. Each test runs against a real
/// Postgres container; collaboration-hierarchy tables reset before each test.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class TeamRepositoryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private readonly TeamRepository _repo;
    private readonly OrganizationRepository _orgRepo;

    public TeamRepositoryTests(PostgresFixture fx)
    {
        _fx = fx;
        var options = Options.Create(new DataOptions { PostgresConnectionString = fx.ConnectionString });
        _repo = new TeamRepository(options);
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

    private async Task<(Guid OrgId, Guid OwnerId)> SeedOrgAsync(string slug = "org-1")
    {
        // Derive a unique seed email from the slug so callers that seed multiple
        // orgs in one test don't collide on the users.email unique index.
        var ownerId = await SeedUserAsync($"owner-{slug}@example.com");
        var org = new Organization { Slug = slug, Name = slug, CreatedBy = ownerId };
        await _orgRepo.CreateAsync(org);
        return (org.Id, ownerId);
    }

    [Fact]
    public void Should_Throw_When_OptionsIsNull()
    {
        Action act = () => new TeamRepository(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task CreateAsync_Should_PopulateIdAndCreatedAt()
    {
        var (orgId, ownerId) = await SeedOrgAsync();
        var team = new Team { OrganizationId = orgId, Slug = "platform", Name = "Platform", CreatedBy = ownerId };

        var id = await _repo.CreateAsync(team);

        id.Should().NotBe(Guid.Empty);
        team.Id.Should().Be(id);
        team.CreatedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task GetByIdAsync_Should_ReturnPersistedRow()
    {
        var (orgId, ownerId) = await SeedOrgAsync();
        var team = new Team { OrganizationId = orgId, Slug = "platform", Name = "Platform", CreatedBy = ownerId };
        await _repo.CreateAsync(team);

        var fetched = await _repo.GetByIdAsync(team.Id);

        fetched.Should().NotBeNull();
        fetched!.OrganizationId.Should().Be(orgId);
        fetched.Slug.Should().Be("platform");
    }

    [Fact]
    public async Task GetBySlugAsync_Should_BeScopedPerOrganization_AndCaseInsensitive()
    {
        var (orgAId, ownerId) = await SeedOrgAsync("org-a");
        var (orgBId, _) = await SeedOrgAsync("org-b");

        await _repo.CreateAsync(new Team { OrganizationId = orgAId, Slug = "Platform", Name = "A", CreatedBy = ownerId });
        await _repo.CreateAsync(new Team { OrganizationId = orgBId, Slug = "platform", Name = "B", CreatedBy = ownerId });

        var fromA = await _repo.GetBySlugAsync(orgAId, "PLATFORM");
        var fromB = await _repo.GetBySlugAsync(orgBId, "platform");

        fromA.Should().NotBeNull();
        fromB.Should().NotBeNull();
        fromA!.Id.Should().NotBe(fromB!.Id);
        fromA.Name.Should().Be("A");
        fromB.Name.Should().Be("B");
    }

    [Fact]
    public async Task GetBySlugAsync_Should_Throw_When_SlugBlank()
    {
        Func<Task> act = () => _repo.GetBySlugAsync(Guid.NewGuid(), " ");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetByOrganizationAsync_Should_ReturnSortedBySlug()
    {
        var (orgId, ownerId) = await SeedOrgAsync();
        await _repo.CreateAsync(new Team { OrganizationId = orgId, Slug = "zeta", Name = "Z", CreatedBy = ownerId });
        await _repo.CreateAsync(new Team { OrganizationId = orgId, Slug = "alpha", Name = "A", CreatedBy = ownerId });

        var rows = await _repo.GetByOrganizationAsync(orgId);

        rows.Select(r => r.Slug).Should().ContainInOrder("alpha", "zeta");
    }

    [Fact]
    public async Task UpdateAsync_Should_MutateSlugAndName_NotOrganizationId()
    {
        var (orgId, ownerId) = await SeedOrgAsync();
        var (otherOrgId, _) = await SeedOrgAsync("other");
        var team = new Team { OrganizationId = orgId, Slug = "old", Name = "Old", CreatedBy = ownerId };
        await _repo.CreateAsync(team);

        // Mutate every field the caller might try to change. The repo should only
        // honour slug + name; organization_id stays put.
        team.Slug = "new";
        team.Name = "New";
        team.OrganizationId = otherOrgId;
        await _repo.UpdateAsync(team);

        var fetched = await _repo.GetByIdAsync(team.Id);
        fetched!.Slug.Should().Be("new");
        fetched.Name.Should().Be("New");
        fetched.OrganizationId.Should().Be(orgId, "organization_id is immutable on update");
    }

    [Fact]
    public async Task UpdateAsync_Should_ReturnFalse_When_NotFound()
    {
        var ghost = new Team
        {
            Id = Guid.NewGuid(),
            OrganizationId = Guid.NewGuid(),
            Slug = "g",
            Name = "G",
            CreatedBy = Guid.NewGuid(),
        };

        (await _repo.UpdateAsync(ghost)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_Should_RemoveRow()
    {
        var (orgId, ownerId) = await SeedOrgAsync();
        var team = new Team { OrganizationId = orgId, Slug = "doomed", Name = "Doomed", CreatedBy = ownerId };
        await _repo.CreateAsync(team);

        (await _repo.DeleteAsync(team.Id)).Should().BeTrue();
        (await _repo.GetByIdAsync(team.Id)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_Should_ReturnFalse_When_NotFound()
    {
        (await _repo.DeleteAsync(Guid.NewGuid())).Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_Should_RejectDuplicateSlug_WithinOrganization()
    {
        var (orgId, ownerId) = await SeedOrgAsync();
        await _repo.CreateAsync(new Team { OrganizationId = orgId, Slug = "Platform", Name = "P", CreatedBy = ownerId });

        Func<Task> act = () => _repo.CreateAsync(new Team { OrganizationId = orgId, Slug = "platform", Name = "dup", CreatedBy = ownerId });
        await act.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "23505");
    }

    [Fact]
    public async Task CreateAsync_Should_Throw_When_TeamIsNull()
    {
        Func<Task> act = () => _repo.CreateAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task UpdateAsync_Should_Throw_When_TeamIsNull()
    {
        Func<Task> act = () => _repo.UpdateAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task DeletingOrganization_Should_CascadeTeams()
    {
        var (orgId, ownerId) = await SeedOrgAsync();
        var team = new Team { OrganizationId = orgId, Slug = "child", Name = "Child", CreatedBy = ownerId };
        await _repo.CreateAsync(team);

        (await _orgRepo.DeleteAsync(orgId)).Should().BeTrue();
        (await _repo.GetByIdAsync(team.Id)).Should().BeNull();
    }
}
