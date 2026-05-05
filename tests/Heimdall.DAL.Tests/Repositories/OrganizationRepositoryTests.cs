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
/// Integration tests for <see cref="OrganizationRepository"/>. Each test runs against
/// a real Postgres container provided by <see cref="PostgresFixture"/>, with the
/// collaboration-hierarchy tables reset before each test.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class OrganizationRepositoryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private readonly OrganizationRepository _repo;

    public OrganizationRepositoryTests(PostgresFixture fx)
    {
        _fx = fx;
        var options = Options.Create(new DataOptions { PostgresConnectionString = fx.ConnectionString });
        _repo = new OrganizationRepository(options);
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

    [Fact]
    public void Should_Throw_When_OptionsIsNull()
    {
        Action act = () => new OrganizationRepository(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task CreateAsync_Should_PopulateIdAndCreatedAt()
    {
        var ownerId = await SeedUserAsync();
        var org = new Organization { Slug = "heimdall", Name = "Heimdall", CreatedBy = ownerId };

        var id = await _repo.CreateAsync(org);

        id.Should().NotBe(Guid.Empty);
        org.Id.Should().Be(id);
        org.CreatedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task GetByIdAsync_Should_ReturnPersistedRow()
    {
        var ownerId = await SeedUserAsync();
        var org = new Organization { Slug = "heimdall", Name = "Heimdall", CreatedBy = ownerId };
        await _repo.CreateAsync(org);

        var fetched = await _repo.GetByIdAsync(org.Id);

        fetched.Should().NotBeNull();
        fetched!.Slug.Should().Be("heimdall");
        fetched.Name.Should().Be("Heimdall");
        fetched.CreatedBy.Should().Be(ownerId);
    }

    [Fact]
    public async Task GetByIdAsync_Should_ReturnNull_When_NotFound()
    {
        var fetched = await _repo.GetByIdAsync(Guid.NewGuid());
        fetched.Should().BeNull();
    }

    [Fact]
    public async Task GetBySlugAsync_Should_BeCaseInsensitive()
    {
        var ownerId = await SeedUserAsync();
        await _repo.CreateAsync(new Organization { Slug = "Heimdall", Name = "Heimdall", CreatedBy = ownerId });

        var lowercase = await _repo.GetBySlugAsync("heimdall");
        var uppercase = await _repo.GetBySlugAsync("HEIMDALL");

        lowercase.Should().NotBeNull();
        uppercase.Should().NotBeNull();
        lowercase!.Id.Should().Be(uppercase!.Id);
    }

    [Fact]
    public async Task GetBySlugAsync_Should_Throw_When_SlugBlank()
    {
        Func<Task> act = () => _repo.GetBySlugAsync(" ");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetAllAsync_Should_ReturnSortedBySlug()
    {
        var ownerId = await SeedUserAsync();
        await _repo.CreateAsync(new Organization { Slug = "zeta", Name = "Z", CreatedBy = ownerId });
        await _repo.CreateAsync(new Organization { Slug = "alpha", Name = "A", CreatedBy = ownerId });
        await _repo.CreateAsync(new Organization { Slug = "mu", Name = "M", CreatedBy = ownerId });

        var rows = await _repo.GetAllAsync();

        rows.Select(r => r.Slug).Should().ContainInOrder("alpha", "mu", "zeta");
    }

    [Fact]
    public async Task UpdateAsync_Should_MutateSlugAndName()
    {
        var ownerId = await SeedUserAsync();
        var org = new Organization { Slug = "old-slug", Name = "Old", CreatedBy = ownerId };
        await _repo.CreateAsync(org);

        org.Slug = "new-slug";
        org.Name = "New";
        var updated = await _repo.UpdateAsync(org);

        updated.Should().BeTrue();
        var fetched = await _repo.GetByIdAsync(org.Id);
        fetched!.Slug.Should().Be("new-slug");
        fetched.Name.Should().Be("New");
    }

    [Fact]
    public async Task UpdateAsync_Should_ReturnFalse_When_NotFound()
    {
        var ownerId = await SeedUserAsync();
        var ghost = new Organization
        {
            Id = Guid.NewGuid(),
            Slug = "ghost",
            Name = "Ghost",
            CreatedBy = ownerId,
        };

        var updated = await _repo.UpdateAsync(ghost);
        updated.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_Should_RemoveRow()
    {
        var ownerId = await SeedUserAsync();
        var org = new Organization { Slug = "doomed", Name = "Doomed", CreatedBy = ownerId };
        await _repo.CreateAsync(org);

        var deleted = await _repo.DeleteAsync(org.Id);

        deleted.Should().BeTrue();
        (await _repo.GetByIdAsync(org.Id)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_Should_ReturnFalse_When_NotFound()
    {
        var deleted = await _repo.DeleteAsync(Guid.NewGuid());
        deleted.Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_Should_RejectDuplicateSlug_CaseInsensitive()
    {
        var ownerId = await SeedUserAsync();
        await _repo.CreateAsync(new Organization { Slug = "Heimdall", Name = "Heimdall", CreatedBy = ownerId });

        Func<Task> act = () => _repo.CreateAsync(new Organization { Slug = "heimdall", Name = "dup", CreatedBy = ownerId });
        await act.Should().ThrowAsync<PostgresException>()
            .Where(ex => ex.SqlState == "23505");
    }

    [Fact]
    public async Task CreateAsync_Should_Throw_When_OrganizationIsNull()
    {
        Func<Task> act = () => _repo.CreateAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task UpdateAsync_Should_Throw_When_OrganizationIsNull()
    {
        Func<Task> act = () => _repo.UpdateAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
