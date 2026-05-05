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
/// Integration tests for <see cref="OrganizationMemberRepository"/>. Each test runs
/// against a real Postgres container; collaboration-hierarchy and membership tables
/// reset before each test.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class OrganizationMemberRepositoryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private readonly OrganizationMemberRepository _repo;
    private readonly OrganizationRepository _orgRepo;

    public OrganizationMemberRepositoryTests(PostgresFixture fx)
    {
        _fx = fx;
        var options = Options.Create(new DataOptions { PostgresConnectionString = fx.ConnectionString });
        _repo = new OrganizationMemberRepository(options);
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

    private async Task<Guid> SeedOrgAsync(Guid ownerId, string slug = "org")
    {
        var org = new Organization { Slug = slug, Name = slug, CreatedBy = ownerId };
        await _orgRepo.CreateAsync(org);
        return org.Id;
    }

    [Fact]
    public void Should_Throw_When_OptionsIsNull()
    {
        Action act = () => new OrganizationMemberRepository(null!);
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
        var orgId = await SeedOrgAsync(alice);

        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _repo.AddAsync(new OrganizationMember
        {
            UserId = bob,
            OrganizationId = orgId,
            Role = "member",
            AddedBy = alice,
        });
        var after = DateTimeOffset.UtcNow.AddMinutes(1);

        var fetched = await _repo.GetAsync(bob, orgId);

        fetched.Should().NotBeNull();
        fetched!.UserId.Should().Be(bob);
        fetched.OrganizationId.Should().Be(orgId);
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
        var orgId = await SeedOrgAsync(alice);

        await _repo.AddAsync(new OrganizationMember
        {
            UserId = bob,
            OrganizationId = orgId,
            Role = "member",
            AddedBy = alice,
        });

        Func<Task> act = () => _repo.AddAsync(new OrganizationMember
        {
            UserId = bob,
            OrganizationId = orgId,
            Role = "admin",
            AddedBy = alice,
        });
        await act.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "23505");
    }

    [Fact]
    public async Task GetByParentAsync_Should_ReturnOnlyMembersOfThatParent_OrderedByAddedAtThenUserId()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var carol = await SeedUserAsync("carol@example.com");
        var dave = await SeedUserAsync("dave@example.com");

        var org1 = await SeedOrgAsync(alice, "org-1");
        var org2 = await SeedOrgAsync(alice, "org-2");

        await _repo.AddAsync(new OrganizationMember { UserId = bob, OrganizationId = org1, Role = "member", AddedBy = alice });
        await _repo.AddAsync(new OrganizationMember { UserId = carol, OrganizationId = org1, Role = "admin", AddedBy = alice });
        await _repo.AddAsync(new OrganizationMember { UserId = dave, OrganizationId = org1, Role = "viewer", AddedBy = alice });
        await _repo.AddAsync(new OrganizationMember { UserId = bob, OrganizationId = org2, Role = "owner", AddedBy = alice });

        var rows = await _repo.GetByParentAsync(org1);

        rows.Should().HaveCount(3);
        rows.Select(r => r.UserId).Should().Equal(bob, carol, dave);
        rows.Should().BeInAscendingOrder(r => r.AddedAt);
    }

    [Fact]
    public async Task GetByUserAsync_Should_ReturnOnlyThatUsersMemberships()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var carol = await SeedUserAsync("carol@example.com");

        var org1 = await SeedOrgAsync(alice, "org-1");
        var org2 = await SeedOrgAsync(alice, "org-2");
        var org3 = await SeedOrgAsync(alice, "org-3");

        await _repo.AddAsync(new OrganizationMember { UserId = bob, OrganizationId = org1, Role = "member", AddedBy = alice });
        await _repo.AddAsync(new OrganizationMember { UserId = bob, OrganizationId = org2, Role = "admin", AddedBy = alice });
        await _repo.AddAsync(new OrganizationMember { UserId = carol, OrganizationId = org3, Role = "viewer", AddedBy = alice });

        var rows = await _repo.GetByUserAsync(bob);

        rows.Should().HaveCount(2);
        rows.Select(r => r.OrganizationId).Should().BeEquivalentTo(new[] { org1, org2 });
        rows.Should().BeInAscendingOrder(r => r.AddedAt);
    }

    [Fact]
    public async Task UpdateRoleAsync_Should_ChangeRole_AndReturnTrue()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var orgId = await SeedOrgAsync(alice);

        await _repo.AddAsync(new OrganizationMember { UserId = bob, OrganizationId = orgId, Role = "member", AddedBy = alice });

        (await _repo.UpdateRoleAsync(bob, orgId, "admin")).Should().BeTrue();

        (await _repo.GetAsync(bob, orgId))!.Role.Should().Be("admin");
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
        var orgId = await SeedOrgAsync(alice);

        await _repo.AddAsync(new OrganizationMember { UserId = bob, OrganizationId = orgId, Role = role, AddedBy = alice });

        (await _repo.GetAsync(bob, orgId))!.Role.Should().Be(role);
    }

    [Fact]
    public async Task RemoveAsync_Should_DeleteRow_AndReturnTrue_OnlyOnce()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var orgId = await SeedOrgAsync(alice);

        await _repo.AddAsync(new OrganizationMember { UserId = bob, OrganizationId = orgId, Role = "member", AddedBy = alice });

        (await _repo.RemoveAsync(bob, orgId)).Should().BeTrue();
        (await _repo.GetAsync(bob, orgId)).Should().BeNull();
        (await _repo.RemoveAsync(bob, orgId)).Should().BeFalse();
    }

    [Fact]
    public async Task DeletingParentOrganization_Should_CascadeMemberships()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var orgId = await SeedOrgAsync(alice);

        await _repo.AddAsync(new OrganizationMember { UserId = bob, OrganizationId = orgId, Role = "member", AddedBy = alice });

        (await _orgRepo.DeleteAsync(orgId)).Should().BeTrue();
        (await _repo.GetAsync(bob, orgId)).Should().BeNull();
    }

    [Fact]
    public async Task DeletingMemberUser_Should_CascadeMemberships()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var orgId = await SeedOrgAsync(alice);

        await _repo.AddAsync(new OrganizationMember { UserId = bob, OrganizationId = orgId, Role = "member", AddedBy = alice });

        await using (var conn = new NpgsqlConnection(_fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("DELETE FROM users WHERE id = @Id", new { Id = bob });
        }

        (await _repo.GetAsync(bob, orgId)).Should().BeNull();
    }

    [Fact]
    public async Task DeletingInviterUser_Should_BeBlockedByRestrictFk()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var carol = await SeedUserAsync("carol@example.com");
        var orgId = await SeedOrgAsync(alice);

        // Carol is the inviter (added_by) but neither the org owner nor the
        // member. Deleting carol therefore can only trip the
        // organization_members.added_by FK; it isolates the RESTRICT we are
        // asserting on.
        await _repo.AddAsync(new OrganizationMember { UserId = bob, OrganizationId = orgId, Role = "member", AddedBy = carol });

        Func<Task> act = async () =>
        {
            await using var conn = new NpgsqlConnection(_fx.ConnectionString);
            await conn.OpenAsync();
            await conn.ExecuteAsync("DELETE FROM users WHERE id = @Id", new { Id = carol });
        };

        // ON DELETE RESTRICT raises SQLSTATE 23503 (foreign_key_violation) in
        // PostgreSQL — both INSERT-with-bogus-FK and DELETE-blocked-by-RESTRICT
        // share that code; PostgreSQL does not emit the SQL-standard 23001.
        await act.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "23001" || ex.SqlState == "23503");
    }
}
