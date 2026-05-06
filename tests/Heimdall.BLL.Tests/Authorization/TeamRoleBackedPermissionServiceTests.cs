using FluentAssertions;
using Heimdall.BLL.Authorization;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Moq;

namespace Heimdall.BLL.Tests.Authorization;

/// <summary>
/// Unit tests for <see cref="TeamRoleBackedPermissionService"/>. Each public
/// method's matrix from <c>docs/proposals/team-collaboration.md</c> §5 is exercised
/// cell-by-cell, plus the deny-closed unknown-role / missing-membership paths and
/// the <c>system_admin == true</c> short-circuit at the top of every method.
/// </summary>
public class TeamRoleBackedPermissionServiceTests
{
    private static readonly Guid Actor = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TeamId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherUser = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid DestinationTeam = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly Mock<ITeamMemberRepository> _teamMembers = new(MockBehavior.Strict);
    private readonly Mock<IUserLookup> _userLookup = new(MockBehavior.Strict);

    private TeamRoleBackedPermissionService CreateSut() => new(_teamMembers.Object, _userLookup.Object);

    private void SetupSystemAdmin(bool isAdmin) =>
        _userLookup
            .Setup(u => u.IsSystemAdminAsync(Actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(isAdmin);

    private void SetupRole(Guid teamId, TeamMemberRole? role)
    {
        _teamMembers
            .Setup(r => r.GetAsync(Actor, teamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(role is null
                ? null
                : new TeamMember
                {
                    UserId = Actor,
                    TeamId = teamId,
                    Role = role.Value,
                    AddedBy = Actor,
                });
    }

    private static Ticket NewTicket(Guid? assignee = null, Guid? teamId = null) =>
        new()
        {
            Id = 1,
            Title = "T",
            TeamId = teamId ?? TeamId,
            ProjectId = Guid.NewGuid(),
            ReporterId = Guid.NewGuid(),
            AssigneeId = assignee,
        };

    // ------------------------------------------------------------------
    // Constructor guards.
    // ------------------------------------------------------------------
    [Fact]
    public void Should_Throw_When_TeamMemberRepositoryIsNull()
    {
        Action act = () => new TeamRoleBackedPermissionService(null!, _userLookup.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_UserLookupIsNull()
    {
        Action act = () => new TeamRoleBackedPermissionService(_teamMembers.Object, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ------------------------------------------------------------------
    // §5.1 Queue visibility.
    // ------------------------------------------------------------------
    [Theory]
    [InlineData(TeamMemberRole.Manager, true)]
    [InlineData(TeamMemberRole.TeamLead, true)]
    [InlineData(TeamMemberRole.Member, true)]
    [InlineData(TeamMemberRole.Viewer, true)]
    public async Task CanViewTeamQueueAsync_Should_ReturnExpected_When_RoleSet(
        TeamMemberRole role,
        bool expected)
    {
        SetupSystemAdmin(false);
        SetupRole(TeamId, role);

        var actual = await CreateSut().CanViewTeamQueueAsync(Actor, TeamId, default);

        actual.Should().Be(expected);
    }

    [Fact]
    public async Task CanViewTeamQueueAsync_Should_ReturnFalse_When_NotAMember()
    {
        SetupSystemAdmin(false);
        SetupRole(TeamId, null);

        var actual = await CreateSut().CanViewTeamQueueAsync(Actor, TeamId, default);

        actual.Should().BeFalse();
    }

    [Fact]
    public async Task CanViewTeamQueueAsync_Should_ReturnTrue_When_SystemAdminAndNotAMember()
    {
        // §5.1 row: system_admin == true grants cross-team visibility even with no membership.
        SetupSystemAdmin(true);

        var actual = await CreateSut().CanViewTeamQueueAsync(Actor, TeamId, default);

        actual.Should().BeTrue();
        // Short-circuit must skip the team-member lookup entirely.
        _teamMembers.Verify(
            r => r.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CanViewTeamQueueAsync_Should_ReturnFalse_When_RoleIsUnknownEnumValue()
    {
        // Deny-closed: a value outside TeamMemberRole's declared cases (e.g. a future
        // enum entry the impl doesn't recognise yet) must collapse to false.
        SetupSystemAdmin(false);
        SetupRole(TeamId, (TeamMemberRole)999);

        var actual = await CreateSut().CanViewTeamQueueAsync(Actor, TeamId, default);

        actual.Should().BeFalse();
    }

    [Fact]
    public async Task CanViewTeamQueueAsync_Should_ReturnFalse_When_UserLookupReturnsFalseAndMembershipIsNull()
    {
        // "Unknown user" surfaces from IUserLookup as IsSystemAdminAsync == false (deny-closed
        // inside the lookup itself) plus a null membership row — still maps to false.
        SetupSystemAdmin(false);
        SetupRole(TeamId, null);

        var actual = await CreateSut().CanViewTeamQueueAsync(Actor, TeamId, default);

        actual.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // §5.2 Routing.
    // ------------------------------------------------------------------
    [Theory]
    [InlineData(TeamMemberRole.Manager, true)]
    [InlineData(TeamMemberRole.TeamLead, true)]
    [InlineData(TeamMemberRole.Viewer, false)]
    public async Task CanRouteTicketAsync_Should_FollowMatrix_When_RoleNotMember(
        TeamMemberRole role,
        bool expected)
    {
        SetupSystemAdmin(false);
        SetupRole(TeamId, role);
        var ticket = NewTicket();

        var actual = await CreateSut().CanRouteTicketAsync(Actor, ticket, DestinationTeam, default);

        actual.Should().Be(expected);
    }

    [Fact]
    public async Task CanRouteTicketAsync_Should_ReturnTrue_When_MemberAndAssigneeIsActor()
    {
        SetupSystemAdmin(false);
        SetupRole(TeamId, TeamMemberRole.Member);
        var ticket = NewTicket(assignee: Actor);

        var actual = await CreateSut().CanRouteTicketAsync(Actor, ticket, DestinationTeam, default);

        actual.Should().BeTrue();
    }

    [Fact]
    public async Task CanRouteTicketAsync_Should_ReturnFalse_When_MemberAndAssigneeIsNotActor()
    {
        SetupSystemAdmin(false);
        SetupRole(TeamId, TeamMemberRole.Member);
        var ticket = NewTicket(assignee: OtherUser);

        var actual = await CreateSut().CanRouteTicketAsync(Actor, ticket, DestinationTeam, default);

        actual.Should().BeFalse();
    }

    [Fact]
    public async Task CanRouteTicketAsync_Should_ReturnFalse_When_MemberAndTicketUnassigned()
    {
        SetupSystemAdmin(false);
        SetupRole(TeamId, TeamMemberRole.Member);
        var ticket = NewTicket(assignee: null);

        var actual = await CreateSut().CanRouteTicketAsync(Actor, ticket, DestinationTeam, default);

        actual.Should().BeFalse();
    }

    [Fact]
    public async Task CanRouteTicketAsync_Should_ReturnFalse_When_NotAMember()
    {
        SetupSystemAdmin(false);
        SetupRole(TeamId, null);
        var ticket = NewTicket();

        var actual = await CreateSut().CanRouteTicketAsync(Actor, ticket, DestinationTeam, default);

        actual.Should().BeFalse();
    }

    [Fact]
    public async Task CanRouteTicketAsync_Should_ReturnTrue_When_SystemAdminAndNotAMember()
    {
        SetupSystemAdmin(true);

        var actual = await CreateSut().CanRouteTicketAsync(Actor, NewTicket(), DestinationTeam, default);

        actual.Should().BeTrue();
        _teamMembers.Verify(
            r => r.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CanRouteTicketAsync_Should_NotConsultDestinationTeamRole_When_SourceRolePermits()
    {
        // §5.2 is explicit: the destination team's role is NOT checked. Pin that by
        // setting a permitting source-team role and giving the actor zero membership
        // anywhere else; CanRouteTicketAsync must still return true and must not
        // probe the destination team.
        SetupSystemAdmin(false);
        SetupRole(TeamId, TeamMemberRole.Manager);
        // No setup for DestinationTeam — strict mock would fail if it were probed.

        var actual = await CreateSut().CanRouteTicketAsync(Actor, NewTicket(), DestinationTeam, default);

        actual.Should().BeTrue();
        _teamMembers.Verify(
            r => r.GetAsync(Actor, DestinationTeam, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CanRouteTicketAsync_Should_ReturnFalse_When_RoleIsUnknownEnumValue()
    {
        SetupSystemAdmin(false);
        SetupRole(TeamId, (TeamMemberRole)999);

        var actual = await CreateSut().CanRouteTicketAsync(Actor, NewTicket(), DestinationTeam, default);

        actual.Should().BeFalse();
    }

    [Fact]
    public async Task CanRouteTicketAsync_Should_Throw_When_TicketIsNull()
    {
        // No setups — null guard runs before any I/O.
        Func<Task> act = () => CreateSut().CanRouteTicketAsync(Actor, null!, DestinationTeam, default);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ------------------------------------------------------------------
    // §5.3 Self-assign / claim.
    // ------------------------------------------------------------------
    [Fact]
    public async Task CanAssignTicketAsync_Should_ReturnTrue_When_ManagerClaimsUnassignedTicket()
    {
        SetupSystemAdmin(false);
        SetupRole(TeamId, TeamMemberRole.Manager);

        var actual = await CreateSut()
            .CanAssignTicketAsync(Actor, NewTicket(assignee: null), Actor, default);

        actual.Should().BeTrue();
    }

    [Fact]
    public async Task CanAssignTicketAsync_Should_ReturnTrue_When_ManagerStealsFromOther()
    {
        // §5.3 row: manager / team_lead can re-claim (steal) from a current assignee.
        SetupSystemAdmin(false);
        SetupRole(TeamId, TeamMemberRole.Manager);

        var actual = await CreateSut()
            .CanAssignTicketAsync(Actor, NewTicket(assignee: OtherUser), Actor, default);

        actual.Should().BeTrue();
    }

    [Fact]
    public async Task CanAssignTicketAsync_Should_ReturnTrue_When_TeamLeadStealsFromOther()
    {
        SetupSystemAdmin(false);
        SetupRole(TeamId, TeamMemberRole.TeamLead);

        var actual = await CreateSut()
            .CanAssignTicketAsync(Actor, NewTicket(assignee: OtherUser), Actor, default);

        actual.Should().BeTrue();
    }

    [Fact]
    public async Task CanAssignTicketAsync_Should_ReturnTrue_When_ManagerAssignsAnotherUser()
    {
        // Override case: the manager assigns the ticket to someone other than themselves.
        SetupSystemAdmin(false);
        SetupRole(TeamId, TeamMemberRole.Manager);

        var actual = await CreateSut()
            .CanAssignTicketAsync(Actor, NewTicket(assignee: null), OtherUser, default);

        actual.Should().BeTrue();
    }

    [Fact]
    public async Task CanAssignTicketAsync_Should_ReturnTrue_When_MemberClaimsUnassignedTicket()
    {
        SetupSystemAdmin(false);
        SetupRole(TeamId, TeamMemberRole.Member);

        var actual = await CreateSut()
            .CanAssignTicketAsync(Actor, NewTicket(assignee: null), Actor, default);

        actual.Should().BeTrue();
    }

    [Fact]
    public async Task CanAssignTicketAsync_Should_ReturnFalse_When_MemberAndTicketAlreadyAssignedToOther()
    {
        SetupSystemAdmin(false);
        SetupRole(TeamId, TeamMemberRole.Member);

        var actual = await CreateSut()
            .CanAssignTicketAsync(Actor, NewTicket(assignee: OtherUser), Actor, default);

        actual.Should().BeFalse();
    }

    [Fact]
    public async Task CanAssignTicketAsync_Should_ReturnFalse_When_MemberAssignsAnotherUser()
    {
        SetupSystemAdmin(false);
        SetupRole(TeamId, TeamMemberRole.Member);

        var actual = await CreateSut()
            .CanAssignTicketAsync(Actor, NewTicket(assignee: null), OtherUser, default);

        actual.Should().BeFalse();
    }

    [Fact]
    public async Task CanAssignTicketAsync_Should_ReturnFalse_When_Viewer()
    {
        SetupSystemAdmin(false);
        SetupRole(TeamId, TeamMemberRole.Viewer);

        var actual = await CreateSut()
            .CanAssignTicketAsync(Actor, NewTicket(assignee: null), Actor, default);

        actual.Should().BeFalse();
    }

    [Fact]
    public async Task CanAssignTicketAsync_Should_ReturnFalse_When_NotAMember()
    {
        SetupSystemAdmin(false);
        SetupRole(TeamId, null);

        var actual = await CreateSut()
            .CanAssignTicketAsync(Actor, NewTicket(assignee: null), Actor, default);

        actual.Should().BeFalse();
    }

    [Fact]
    public async Task CanAssignTicketAsync_Should_ReturnTrue_When_SystemAdminAndNotAMember()
    {
        SetupSystemAdmin(true);

        var actual = await CreateSut()
            .CanAssignTicketAsync(Actor, NewTicket(assignee: OtherUser), OtherUser, default);

        actual.Should().BeTrue();
        _teamMembers.Verify(
            r => r.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CanAssignTicketAsync_Should_ReturnFalse_When_RoleIsUnknownEnumValue()
    {
        SetupSystemAdmin(false);
        SetupRole(TeamId, (TeamMemberRole)999);

        var actual = await CreateSut()
            .CanAssignTicketAsync(Actor, NewTicket(assignee: null), Actor, default);

        actual.Should().BeFalse();
    }

    [Fact]
    public async Task CanAssignTicketAsync_Should_Throw_When_TicketIsNull()
    {
        Func<Task> act = () => CreateSut().CanAssignTicketAsync(Actor, null!, Actor, default);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ------------------------------------------------------------------
    // CanManageTeamMembersAsync — conservative derivation per IPermissionService docs.
    // ------------------------------------------------------------------
    [Theory]
    [InlineData(TeamMemberRole.Manager, true)]
    [InlineData(TeamMemberRole.TeamLead, true)]
    [InlineData(TeamMemberRole.Member, false)]
    [InlineData(TeamMemberRole.Viewer, false)]
    public async Task CanManageTeamMembersAsync_Should_FollowMatrix_When_RoleSet(
        TeamMemberRole role,
        bool expected)
    {
        SetupSystemAdmin(false);
        SetupRole(TeamId, role);

        var actual = await CreateSut().CanManageTeamMembersAsync(Actor, TeamId, default);

        actual.Should().Be(expected);
    }

    [Fact]
    public async Task CanManageTeamMembersAsync_Should_ReturnFalse_When_NotAMember()
    {
        SetupSystemAdmin(false);
        SetupRole(TeamId, null);

        var actual = await CreateSut().CanManageTeamMembersAsync(Actor, TeamId, default);

        actual.Should().BeFalse();
    }

    [Fact]
    public async Task CanManageTeamMembersAsync_Should_ReturnTrue_When_SystemAdminAndNotAMember()
    {
        SetupSystemAdmin(true);

        var actual = await CreateSut().CanManageTeamMembersAsync(Actor, TeamId, default);

        actual.Should().BeTrue();
        _teamMembers.Verify(
            r => r.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CanManageTeamMembersAsync_Should_ReturnFalse_When_RoleIsUnknownEnumValue()
    {
        SetupSystemAdmin(false);
        SetupRole(TeamId, (TeamMemberRole)999);

        var actual = await CreateSut().CanManageTeamMembersAsync(Actor, TeamId, default);

        actual.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // Repository-throws stance. The implementation does NOT catch — the
    // exception propagates. These tests pin that contract so a future
    // change of stance is a deliberate decision visible in CI.
    // ------------------------------------------------------------------
    [Fact]
    public async Task CanViewTeamQueueAsync_Should_PropagateException_When_RepositoryThrows()
    {
        SetupSystemAdmin(false);
        _teamMembers
            .Setup(r => r.GetAsync(Actor, TeamId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transient db hiccup"));

        Func<Task> act = () => CreateSut().CanViewTeamQueueAsync(Actor, TeamId, default);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CanRouteTicketAsync_Should_PropagateException_When_RepositoryThrows()
    {
        SetupSystemAdmin(false);
        _teamMembers
            .Setup(r => r.GetAsync(Actor, TeamId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transient db hiccup"));

        Func<Task> act = () => CreateSut().CanRouteTicketAsync(Actor, NewTicket(), DestinationTeam, default);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CanAssignTicketAsync_Should_PropagateException_When_RepositoryThrows()
    {
        SetupSystemAdmin(false);
        _teamMembers
            .Setup(r => r.GetAsync(Actor, TeamId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transient db hiccup"));

        Func<Task> act = () => CreateSut().CanAssignTicketAsync(Actor, NewTicket(), Actor, default);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CanManageTeamMembersAsync_Should_PropagateException_When_RepositoryThrows()
    {
        SetupSystemAdmin(false);
        _teamMembers
            .Setup(r => r.GetAsync(Actor, TeamId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transient db hiccup"));

        Func<Task> act = () => CreateSut().CanManageTeamMembersAsync(Actor, TeamId, default);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
