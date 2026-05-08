using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Heimdall.BLL.Authorization;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.BLL.Mapping;
using Heimdall.BLL.Services;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Heimdall.DAL.Auditing;
using Heimdall.DAL.Configuration;
using Heimdall.DAL.Repositories;
using Heimdall.DAL.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Heimdall.DAL.Tests.Acceptance;

/// <summary>
/// Phase 2.10 step 28 — full §5 policy-matrix coverage exercised end-to-end
/// against a real Postgres Testcontainer.
/// </summary>
/// <remarks>
/// <para>
/// Companion to <see cref="AuditTransactionAtomicityTests"/>: that file proves the
/// transactional contract of <c>tickets</c> UPDATE + <c>audit_events</c> INSERT;
/// this file proves the policy matrix from
/// <c>docs/proposals/team-collaboration.md</c> §5 against the same real
/// <see cref="TicketService"/> stack — real
/// <see cref="TeamRoleBackedPermissionService"/>, real
/// <see cref="TicketRepository"/>, real <see cref="AuditEventWriter"/>, real
/// <see cref="NpgsqlConnectionFactory"/>. The unit-test matrix in
/// <c>tests/Heimdall.BLL.Tests/Services/TicketServiceTests.cs</c> exercises the
/// same cells against mocks; this suite's distinct value is verifying the
/// behaviour the mocks abstract away — that the audit row really lands in
/// <c>audit_events</c> and that the deny path leaves the table untouched.
/// </para>
/// <para>
/// §5.1 (queue visibility) is intentionally out of scope: no production call site
/// gates queue listing through <see cref="ITicketService"/> today — the
/// <c>CanViewTeamQueueAsync</c> check lives in the UI layer's queue page, and
/// <see cref="ITicketService.GetByTeamAsync"/> bypasses the permission service by
/// design (the BLL provides the data; the page provides the gate). When/if a
/// gated server-side surface lands, its tests belong here.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PolicyMatrixIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private readonly TicketService _service;
    private readonly NpgsqlConnectionFactory _connectionFactory;
    private readonly TicketRepository _ticketRepo;
    private readonly OrganizationRepository _orgRepo;
    private readonly TeamRepository _teamRepo;
    private readonly ProjectRepository _projectRepo;
    private readonly TeamMemberRepository _teamMemberRepo;

    private Guid _orgId;
    private Guid _sourceTeamId;
    private Guid _destTeamId;
    private Guid _projectId;
    private Guid _reporterId;
    private Guid _otherAssigneeId;
    private int _unassignedTicketId;
    private int _assignedToOtherTicketId;

    public PolicyMatrixIntegrationTests(PostgresFixture fx)
    {
        _fx = fx;
        var options = Options.Create(new DataOptions { PostgresConnectionString = fx.ConnectionString });

        _ticketRepo = new TicketRepository(options);
        _orgRepo = new OrganizationRepository(options);
        _teamRepo = new TeamRepository(options);
        _projectRepo = new ProjectRepository(options);
        _teamMemberRepo = new TeamMemberRepository(options);
        _connectionFactory = new NpgsqlConnectionFactory(options);

        var permissions = new TeamRoleBackedPermissionService(
            _teamMemberRepo,
            new UserLookup(options));

        _service = new TicketService(
            _ticketRepo,
            new NoOpCacheService(),
            new TicketMapper(),
            permissions,
            _connectionFactory,
            new AuditEventWriter(options),
            NoOpTupleWriter.Instance,
            NullLogger<TicketService>.Instance);
    }

    /// <summary>
    /// Minimal in-test <see cref="ITupleWriter"/> stub. The integration test exercises
    /// the relational write path only; OpenFGA tuple emission is verified separately.
    /// </summary>
    private sealed class NoOpTupleWriter : ITupleWriter
    {
        public static readonly NoOpTupleWriter Instance = new();

        public Task WriteAsync(TupleKey single, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task WriteAsync(
            IReadOnlyList<TupleKey> writes,
            IReadOnlyList<TupleKey> deletes,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReplaceAsync(
            TupleKey? delete,
            TupleKey? write,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public async Task InitializeAsync()
    {
        await _fx.ResetTicketsAndCollaborationTablesAsync();

        _reporterId = await SeedUserAsync("reporter@example.com");
        _otherAssigneeId = await SeedUserAsync("other-assignee@example.com");

        var org = new Organization { Slug = "policy-matrix-org", Name = "Org", CreatedBy = _reporterId };
        await _orgRepo.CreateAsync(org);
        _orgId = org.Id;

        var src = new Team { OrganizationId = _orgId, Slug = "src", Name = "Source", CreatedBy = _reporterId };
        await _teamRepo.CreateAsync(src);
        _sourceTeamId = src.Id;

        var dst = new Team { OrganizationId = _orgId, Slug = "dst", Name = "Destination", CreatedBy = _reporterId };
        await _teamRepo.CreateAsync(dst);
        _destTeamId = dst.Id;

        var project = new Project { TeamId = _sourceTeamId, Slug = "proj", Name = "Proj", CreatedBy = _reporterId };
        await _projectRepo.CreateAsync(project);
        _projectId = project.Id;

        _unassignedTicketId = await CreateTicketAsync(assigneeId: null);
        _assignedToOtherTicketId = await CreateTicketAsync(assigneeId: _otherAssigneeId);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // -------------------- §5.2 Routing matrix --------------------

    [Theory]
    [InlineData(TeamMemberRole.Manager)]
    [InlineData(TeamMemberRole.TeamLead)]
    public async Task Should_AllowRouteAndWriteAudit_When_ActorIsLeadershipRoleOnSourceTeam(TeamMemberRole role)
    {
        var actorId = await SeedActorAsync("actor-route-leadership-" + role + "@example.com");
        await SeedTeamMemberAsync(actorId, _sourceTeamId, role);

        var applied = await _service.RouteTicketAsync(actorId, _unassignedTicketId, _destTeamId, CancellationToken.None);

        applied.Should().BeTrue();
        (await ReadTeamIdAsync(_unassignedTicketId)).Should().Be(_destTeamId);
        (await CountAuditEventsAsync("ticket_routed", actorId, _unassignedTicketId)).Should().Be(1);
    }

    [Fact]
    public async Task Should_AllowRouteAndWriteAudit_When_MemberRoutesOwnAssignedTicket()
    {
        var actorId = await SeedActorAsync("actor-route-member-self@example.com");
        await SeedTeamMemberAsync(actorId, _sourceTeamId, TeamMemberRole.Member);

        // §5.2 member row: only own assigned tickets. Stamp the actor as the
        // current assignee on the unassigned ticket via a direct UPDATE so this
        // test owns a fresh "assigned to actor" precondition without touching
        // the BLL's assign path (which has its own permission gate we don't
        // want entangled in this assertion).
        await SetAssigneeDirectlyAsync(_unassignedTicketId, actorId);

        var applied = await _service.RouteTicketAsync(actorId, _unassignedTicketId, _destTeamId, CancellationToken.None);

        applied.Should().BeTrue();
        (await ReadTeamIdAsync(_unassignedTicketId)).Should().Be(_destTeamId);
        (await CountAuditEventsAsync("ticket_routed", actorId, _unassignedTicketId)).Should().Be(1);
    }

    [Fact]
    public async Task Should_DenyRouteAndWriteNoAudit_When_MemberRoutesTicketAssignedToSomeoneElse()
    {
        var actorId = await SeedActorAsync("actor-route-member-other@example.com");
        await SeedTeamMemberAsync(actorId, _sourceTeamId, TeamMemberRole.Member);

        Func<Task> act = () => _service.RouteTicketAsync(actorId, _assignedToOtherTicketId, _destTeamId, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        (await ReadTeamIdAsync(_assignedToOtherTicketId)).Should().Be(_sourceTeamId);
        (await CountAuditEventsAsync("ticket_routed", actorId, _assignedToOtherTicketId)).Should().Be(0);
    }

    [Theory]
    [InlineData(TeamMemberRole.Viewer)]
    public async Task Should_DenyRouteAndWriteNoAudit_When_ActorIsViewerOnSourceTeam(TeamMemberRole role)
    {
        var actorId = await SeedActorAsync("actor-route-viewer-" + role + "@example.com");
        await SeedTeamMemberAsync(actorId, _sourceTeamId, role);

        Func<Task> act = () => _service.RouteTicketAsync(actorId, _unassignedTicketId, _destTeamId, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        (await ReadTeamIdAsync(_unassignedTicketId)).Should().Be(_sourceTeamId);
        (await CountAuditEventsAsync("ticket_routed", actorId, _unassignedTicketId)).Should().Be(0);
    }

    [Fact]
    public async Task Should_DenyRouteAndWriteNoAudit_When_ActorIsNonMemberOfSourceTeam()
    {
        var actorId = await SeedActorAsync("actor-route-nonmember@example.com");
        // Intentionally no team_members row.

        Func<Task> act = () => _service.RouteTicketAsync(actorId, _unassignedTicketId, _destTeamId, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        (await ReadTeamIdAsync(_unassignedTicketId)).Should().Be(_sourceTeamId);
        (await CountAuditEventsAsync("ticket_routed", actorId, _unassignedTicketId)).Should().Be(0);
    }

    [Fact]
    public async Task Should_AllowRouteAndWriteAudit_When_ActorIsSystemAdminWithoutMembership()
    {
        var actorId = await SeedActorAsync("actor-route-sysadmin@example.com", systemAdmin: true);
        // Intentionally no team_members row — the system_admin short-circuit
        // (§5 last row) is the only bypass.

        var applied = await _service.RouteTicketAsync(actorId, _unassignedTicketId, _destTeamId, CancellationToken.None);

        applied.Should().BeTrue();
        (await ReadTeamIdAsync(_unassignedTicketId)).Should().Be(_destTeamId);
        (await CountAuditEventsAsync("ticket_routed", actorId, _unassignedTicketId)).Should().Be(1);
    }

    // -------------------- §5.3 Self-assignment / assignment matrix --------------------

    [Theory]
    [InlineData(TeamMemberRole.Manager)]
    [InlineData(TeamMemberRole.TeamLead)]
    public async Task Should_AllowClaimAndWriteAudit_When_ActorIsLeadershipRoleOnTeam(TeamMemberRole role)
    {
        var actorId = await SeedActorAsync("actor-claim-leadership-" + role + "@example.com");
        await SeedTeamMemberAsync(actorId, _sourceTeamId, role);

        var applied = await _service.ClaimTicketAsync(actorId, _unassignedTicketId, CancellationToken.None);

        applied.Should().BeTrue();
        (await ReadAssigneeIdAsync(_unassignedTicketId)).Should().Be(actorId);
        (await CountAuditEventsAsync("ticket_assigned", actorId, _unassignedTicketId)).Should().Be(1);
    }

    [Theory]
    [InlineData(TeamMemberRole.Manager)]
    [InlineData(TeamMemberRole.TeamLead)]
    public async Task Should_AllowReclaimAndWriteAudit_When_ActorIsLeadershipRoleAndTicketAssignedToOther(TeamMemberRole role)
    {
        var actorId = await SeedActorAsync("actor-reclaim-leadership-" + role + "@example.com");
        await SeedTeamMemberAsync(actorId, _sourceTeamId, role);

        var applied = await _service.ClaimTicketAsync(actorId, _assignedToOtherTicketId, CancellationToken.None);

        applied.Should().BeTrue();
        (await ReadAssigneeIdAsync(_assignedToOtherTicketId)).Should().Be(actorId);
        (await CountAuditEventsAsync("ticket_assigned", actorId, _assignedToOtherTicketId)).Should().Be(1);
    }

    [Theory]
    [InlineData(TeamMemberRole.Manager)]
    [InlineData(TeamMemberRole.TeamLead)]
    public async Task Should_AllowAssignToOtherAndWriteAudit_When_ActorIsLeadershipRole(TeamMemberRole role)
    {
        var actorId = await SeedActorAsync("actor-assign-leadership-" + role + "@example.com");
        await SeedTeamMemberAsync(actorId, _sourceTeamId, role);

        var targetId = await SeedActorAsync("assign-target-leadership-" + role + "@example.com");

        var applied = await _service.AssignTicketAsync(actorId, _unassignedTicketId, targetId, CancellationToken.None);

        applied.Should().BeTrue();
        (await ReadAssigneeIdAsync(_unassignedTicketId)).Should().Be(targetId);
        (await CountAuditEventsAsync("ticket_assigned", actorId, _unassignedTicketId)).Should().Be(1);
    }

    [Fact]
    public async Task Should_AllowSelfClaimAndWriteAudit_When_MemberClaimsUnassignedTicket()
    {
        var actorId = await SeedActorAsync("actor-claim-member-unassigned@example.com");
        await SeedTeamMemberAsync(actorId, _sourceTeamId, TeamMemberRole.Member);

        var applied = await _service.ClaimTicketAsync(actorId, _unassignedTicketId, CancellationToken.None);

        applied.Should().BeTrue();
        (await ReadAssigneeIdAsync(_unassignedTicketId)).Should().Be(actorId);
        (await CountAuditEventsAsync("ticket_assigned", actorId, _unassignedTicketId)).Should().Be(1);
    }

    [Fact]
    public async Task Should_DenyClaimAndWriteNoAudit_When_MemberClaimsTicketAssignedToOther()
    {
        var actorId = await SeedActorAsync("actor-claim-member-already-assigned@example.com");
        await SeedTeamMemberAsync(actorId, _sourceTeamId, TeamMemberRole.Member);

        Func<Task> act = () => _service.ClaimTicketAsync(actorId, _assignedToOtherTicketId, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        (await ReadAssigneeIdAsync(_assignedToOtherTicketId)).Should().Be(_otherAssigneeId);
        (await CountAuditEventsAsync("ticket_assigned", actorId, _assignedToOtherTicketId)).Should().Be(0);
    }

    [Fact]
    public async Task Should_DenyAssignAndWriteNoAudit_When_MemberAssignsToOther()
    {
        var actorId = await SeedActorAsync("actor-assign-member-other@example.com");
        await SeedTeamMemberAsync(actorId, _sourceTeamId, TeamMemberRole.Member);
        var targetId = await SeedActorAsync("assign-target-member-other@example.com");

        Func<Task> act = () => _service.AssignTicketAsync(actorId, _unassignedTicketId, targetId, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        (await ReadAssigneeIdAsync(_unassignedTicketId)).Should().BeNull();
        (await CountAuditEventsAsync("ticket_assigned", actorId, _unassignedTicketId)).Should().Be(0);
    }

    [Fact]
    public async Task Should_DenyClaimAndWriteNoAudit_When_ActorIsViewer()
    {
        var actorId = await SeedActorAsync("actor-claim-viewer@example.com");
        await SeedTeamMemberAsync(actorId, _sourceTeamId, TeamMemberRole.Viewer);

        Func<Task> act = () => _service.ClaimTicketAsync(actorId, _unassignedTicketId, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        (await ReadAssigneeIdAsync(_unassignedTicketId)).Should().BeNull();
        (await CountAuditEventsAsync("ticket_assigned", actorId, _unassignedTicketId)).Should().Be(0);
    }

    [Fact]
    public async Task Should_DenyClaimAndWriteNoAudit_When_ActorIsNonMember()
    {
        var actorId = await SeedActorAsync("actor-claim-nonmember@example.com");
        // No team_members row.

        Func<Task> act = () => _service.ClaimTicketAsync(actorId, _unassignedTicketId, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        (await ReadAssigneeIdAsync(_unassignedTicketId)).Should().BeNull();
        (await CountAuditEventsAsync("ticket_assigned", actorId, _unassignedTicketId)).Should().Be(0);
    }

    [Fact]
    public async Task Should_AllowAssignAndWriteAudit_When_ActorIsSystemAdminWithoutMembership()
    {
        var actorId = await SeedActorAsync("actor-assign-sysadmin@example.com", systemAdmin: true);
        var targetId = await SeedActorAsync("assign-target-sysadmin@example.com");

        var applied = await _service.AssignTicketAsync(actorId, _assignedToOtherTicketId, targetId, CancellationToken.None);

        applied.Should().BeTrue();
        (await ReadAssigneeIdAsync(_assignedToOtherTicketId)).Should().Be(targetId);
        (await CountAuditEventsAsync("ticket_assigned", actorId, _assignedToOtherTicketId)).Should().Be(1);
    }

    // -------------------- helpers --------------------

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

    private async Task<Guid> SeedActorAsync(string email, bool systemAdmin = false)
    {
        var id = await SeedUserAsync(email);
        if (systemAdmin)
        {
            await using var conn = new NpgsqlConnection(_fx.ConnectionString);
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "UPDATE users SET system_admin = true WHERE id = @Id",
                new { Id = id });
        }

        return id;
    }

    private async Task SeedTeamMemberAsync(Guid userId, Guid teamId, TeamMemberRole role)
    {
        await _teamMemberRepo.AddAsync(new TeamMember
        {
            UserId = userId,
            TeamId = teamId,
            Role = role,
            AddedBy = _reporterId,
        });
    }

    private async Task<int> CreateTicketAsync(Guid? assigneeId)
    {
        var now = DateTimeOffset.UtcNow;
        var ticket = new Ticket
        {
            Title = "Policy matrix ticket",
            Description = "desc",
            Status = TicketStatus.Open,
            Priority = TicketPriority.Medium,
            ProjectId = _projectId,
            TeamId = _sourceTeamId,
            ReporterId = _reporterId,
            AssigneeId = assigneeId,
            DateCreated = now,
            DateUpdated = now,
        };
        return await _ticketRepo.CreateAsync(ticket);
    }

    private async Task SetAssigneeDirectlyAsync(int ticketId, Guid? assigneeId)
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "UPDATE tickets SET assignee_id = @AssigneeId WHERE id = @Id",
            new { AssigneeId = assigneeId, Id = ticketId });
    }

    private async Task<Guid> ReadTeamIdAsync(int ticketId)
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        return await conn.QuerySingleAsync<Guid>(
            "SELECT team_id FROM tickets WHERE id = @Id",
            new { Id = ticketId });
    }

    private async Task<Guid?> ReadAssigneeIdAsync(int ticketId)
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        return await conn.QuerySingleAsync<Guid?>(
            "SELECT assignee_id FROM tickets WHERE id = @Id",
            new { Id = ticketId });
    }

    private async Task<long> CountAuditEventsAsync(string eventType, Guid actorId, int ticketId)
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM audit_events
              WHERE event_type = @EventType
                AND actor_user_id = @ActorId
                AND target = @Target",
            new
            {
                EventType = eventType,
                ActorId = actorId,
                Target = ticketId.ToString(CultureInfo.InvariantCulture),
            });
    }

    /// <summary>
    /// Minimal in-process <see cref="ICacheService"/> stub used purely to satisfy
    /// <see cref="TicketService"/>'s ctor — none of the route / claim / assign
    /// paths under test consult the cache for reads, and the
    /// <see cref="ICacheService.RemoveAsync"/> invocation on a successful write is
    /// a no-op here so we don't pull Redis (or the StackExchange cache abstraction)
    /// into an integration test that's about Postgres semantics.
    /// </summary>
    private sealed class NoOpCacheService : ICacheService
    {
        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
            where T : class
            => Task.FromResult<T?>(null);

        public Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            where T : class
            => Task.CompletedTask;

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
