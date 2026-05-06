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
/// Integration tests for <see cref="TeamMemberRepository"/>. Each test runs against
/// a real Postgres container; collaboration-hierarchy and membership tables reset
/// before each test.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class TeamMemberRepositoryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private readonly TeamMemberRepository _repo;
    private readonly TeamRepository _teamRepo;
    private readonly OrganizationRepository _orgRepo;

    public TeamMemberRepositoryTests(PostgresFixture fx)
    {
        _fx = fx;
        var options = Options.Create(new DataOptions { PostgresConnectionString = fx.ConnectionString });
        _repo = new TeamMemberRepository(options);
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

    private async Task<Guid> SeedTeamAsync(Guid ownerId, string slug = "alpha")
    {
        var org = (await _orgRepo.GetAllAsync()).FirstOrDefault();
        Guid orgId;
        if (org is null)
        {
            var created = new Organization { Slug = "org", Name = "Org", CreatedBy = ownerId };
            await _orgRepo.CreateAsync(created);
            orgId = created.Id;
        }
        else
        {
            orgId = org.Id;
        }

        var team = new Team { OrganizationId = orgId, Slug = slug, Name = slug, CreatedBy = ownerId };
        await _teamRepo.CreateAsync(team);
        return team.Id;
    }

    [Fact]
    public void Should_Throw_When_OptionsIsNull()
    {
        Action act = () => new TeamMemberRepository(null!);
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
        var teamId = await SeedTeamAsync(alice);

        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _repo.AddAsync(new TeamMember
        {
            UserId = bob,
            TeamId = teamId,
            Role = TeamMemberRole.Member,
            AddedBy = alice,
        });
        var after = DateTimeOffset.UtcNow.AddMinutes(1);

        var fetched = await _repo.GetAsync(bob, teamId);

        fetched.Should().NotBeNull();
        fetched!.UserId.Should().Be(bob);
        fetched.TeamId.Should().Be(teamId);
        fetched.Role.Should().Be(TeamMemberRole.Member);
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
        var teamId = await SeedTeamAsync(alice);

        await _repo.AddAsync(new TeamMember { UserId = bob, TeamId = teamId, Role = TeamMemberRole.Member, AddedBy = alice });

        Func<Task> act = () => _repo.AddAsync(new TeamMember { UserId = bob, TeamId = teamId, Role = TeamMemberRole.Manager, AddedBy = alice });
        await act.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "23505");
    }

    [Fact]
    public async Task GetByParentAsync_Should_ReturnOnlyMembersOfThatTeam_OrderedByAddedAtThenUserId()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var carol = await SeedUserAsync("carol@example.com");
        var dave = await SeedUserAsync("dave@example.com");

        var team1 = await SeedTeamAsync(alice, "alpha");
        var team2 = await SeedTeamAsync(alice, "beta");

        await _repo.AddAsync(new TeamMember { UserId = bob, TeamId = team1, Role = TeamMemberRole.Member, AddedBy = alice });
        await _repo.AddAsync(new TeamMember { UserId = carol, TeamId = team1, Role = TeamMemberRole.TeamLead, AddedBy = alice });
        await _repo.AddAsync(new TeamMember { UserId = dave, TeamId = team1, Role = TeamMemberRole.Viewer, AddedBy = alice });
        await _repo.AddAsync(new TeamMember { UserId = bob, TeamId = team2, Role = TeamMemberRole.Manager, AddedBy = alice });

        // Pin distinct added_at values so the ORDER BY added_at, user_id assertion is deterministic
        // regardless of clock resolution or execution speed.
        await using (var conn = new NpgsqlConnection(_fx.ConnectionString))
        {
            await conn.OpenAsync();
            var t0 = DateTimeOffset.UtcNow.AddSeconds(-2);
            await conn.ExecuteAsync(
                """
                UPDATE team_members
                SET added_at = CASE
                    WHEN user_id = @Bob   THEN @T0
                    WHEN user_id = @Carol THEN @T1
                    WHEN user_id = @Dave  THEN @T2
                END
                WHERE team_id = @Team
                  AND (user_id = @Bob OR user_id = @Carol OR user_id = @Dave)
                """,
                new { Bob = bob, Carol = carol, Dave = dave, Team = team1,
                      T0 = t0, T1 = t0.AddSeconds(1), T2 = t0.AddSeconds(2) });
        }

        var rows = await _repo.GetByParentAsync(team1);

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

        var team1 = await SeedTeamAsync(alice, "alpha");
        var team2 = await SeedTeamAsync(alice, "beta");
        var team3 = await SeedTeamAsync(alice, "gamma");

        await _repo.AddAsync(new TeamMember { UserId = bob, TeamId = team1, Role = TeamMemberRole.Member, AddedBy = alice });
        await _repo.AddAsync(new TeamMember { UserId = bob, TeamId = team2, Role = TeamMemberRole.TeamLead, AddedBy = alice });
        await _repo.AddAsync(new TeamMember { UserId = carol, TeamId = team3, Role = TeamMemberRole.Viewer, AddedBy = alice });

        var rows = await _repo.GetByUserAsync(bob);

        rows.Should().HaveCount(2);
        rows.Select(r => r.TeamId).Should().BeEquivalentTo(new[] { team1, team2 });
        rows.Should().BeInAscendingOrder(r => r.AddedAt);
    }

    [Fact]
    public async Task UpdateRoleAsync_Should_ChangeRole_AndReturnTrue()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var teamId = await SeedTeamAsync(alice);

        await _repo.AddAsync(new TeamMember { UserId = bob, TeamId = teamId, Role = TeamMemberRole.Member, AddedBy = alice });

        (await _repo.UpdateRoleAsync(bob, teamId, TeamMemberRole.TeamLead)).Should().BeTrue();

        (await _repo.GetAsync(bob, teamId))!.Role.Should().Be(TeamMemberRole.TeamLead);
    }

    [Fact]
    public async Task UpdateRoleAsync_Should_ReturnFalse_When_NotFound()
    {
        (await _repo.UpdateRoleAsync(Guid.NewGuid(), Guid.NewGuid(), TeamMemberRole.Member)).Should().BeFalse();
    }

    [Fact]
    public async Task RoundTrip_Should_PreserveEveryEnumValue()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var teamId = await SeedTeamAsync(alice);

        // Insert at Manager (exercises the wire string "manager"), then walk
        // through the remaining three values via UpdateRoleAsync.
        await _repo.AddAsync(new TeamMember { UserId = bob, TeamId = teamId, Role = TeamMemberRole.Manager, AddedBy = alice });
        (await _repo.GetAsync(bob, teamId))!.Role.Should().Be(TeamMemberRole.Manager);

        (await _repo.UpdateRoleAsync(bob, teamId, TeamMemberRole.TeamLead)).Should().BeTrue();
        (await _repo.GetAsync(bob, teamId))!.Role.Should().Be(TeamMemberRole.TeamLead);

        (await _repo.UpdateRoleAsync(bob, teamId, TeamMemberRole.Member)).Should().BeTrue();
        (await _repo.GetAsync(bob, teamId))!.Role.Should().Be(TeamMemberRole.Member);

        (await _repo.UpdateRoleAsync(bob, teamId, TeamMemberRole.Viewer)).Should().BeTrue();
        (await _repo.GetAsync(bob, teamId))!.Role.Should().Be(TeamMemberRole.Viewer);
    }

    [Fact]
    public async Task RemoveAsync_Should_DeleteRow_AndReturnTrue_OnlyOnce()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var teamId = await SeedTeamAsync(alice);

        await _repo.AddAsync(new TeamMember { UserId = bob, TeamId = teamId, Role = TeamMemberRole.Member, AddedBy = alice });

        (await _repo.RemoveAsync(bob, teamId)).Should().BeTrue();
        (await _repo.GetAsync(bob, teamId)).Should().BeNull();
        (await _repo.RemoveAsync(bob, teamId)).Should().BeFalse();
    }

    [Fact]
    public async Task DeletingParentTeam_Should_CascadeMemberships()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var teamId = await SeedTeamAsync(alice);

        await _repo.AddAsync(new TeamMember { UserId = bob, TeamId = teamId, Role = TeamMemberRole.Member, AddedBy = alice });

        (await _teamRepo.DeleteAsync(teamId)).Should().BeTrue();
        (await _repo.GetAsync(bob, teamId)).Should().BeNull();
    }

    [Fact]
    public async Task DeletingMemberUser_Should_CascadeMemberships()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var teamId = await SeedTeamAsync(alice);

        await _repo.AddAsync(new TeamMember { UserId = bob, TeamId = teamId, Role = TeamMemberRole.Member, AddedBy = alice });

        await using (var conn = new NpgsqlConnection(_fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("DELETE FROM users WHERE id = @Id", new { Id = bob });
        }

        (await _repo.GetAsync(bob, teamId)).Should().BeNull();
    }

    [Fact]
    public async Task DeletingInviterUser_Should_BeBlockedByRestrictFk()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var carol = await SeedUserAsync("carol@example.com");
        var teamId = await SeedTeamAsync(alice);

        // Carol is the inviter only — not org/team owner and not the member —
        // so deletion can only trip team_members.added_by RESTRICT.
        await _repo.AddAsync(new TeamMember { UserId = bob, TeamId = teamId, Role = TeamMemberRole.Member, AddedBy = carol });

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
