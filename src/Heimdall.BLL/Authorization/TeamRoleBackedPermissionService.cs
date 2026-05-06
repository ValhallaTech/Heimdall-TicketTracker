using System;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;

namespace Heimdall.BLL.Authorization;

/// <summary>
/// Phase 2 implementation of <see cref="IPermissionService"/>. Reads
/// <c>team_members.role</c> via <see cref="ITeamMemberRepository"/> and the
/// <c>users.system_admin</c> flag via <see cref="IUserLookup"/>; applies the
/// matrices in <c>docs/proposals/team-collaboration.md</c> §5 verbatim.
/// </summary>
/// <remarks>
/// <para>
/// Deny-closed throughout: any <c>null</c> membership row, missing user, or
/// unrecognised role maps to <see langword="false"/>. The
/// <c>system_admin == true</c> short-circuit lives at the top of every public
/// method and is the only caller-visible bypass; call sites never check the
/// flag directly (§6).
/// </para>
/// <para>
/// Phase 3 (<c>docs/proposals/openfga.md</c> step 6) will replace this class
/// with an <c>OpenFgaPermissionService</c> that issues <c>Check()</c> /
/// <c>BatchCheck()</c> against the sidecar; the swap happens via the
/// <c>Authorization:Provider</c> configuration flag and changes no call sites.
/// </para>
/// </remarks>
public sealed class TeamRoleBackedPermissionService : IPermissionService
{
    private readonly ITeamMemberRepository _teamMembers;
    private readonly IUserLookup _userLookup;

    /// <summary>
    /// Initializes a new instance of the <see cref="TeamRoleBackedPermissionService"/> class.
    /// </summary>
    /// <param name="teamMembers">Team-membership repository (provides per-team role lookup).</param>
    /// <param name="userLookup">User-lookup abstraction (provides <c>system_admin</c> flag without an Identity dependency).</param>
    /// <exception cref="ArgumentNullException">If any argument is <c>null</c>.</exception>
    public TeamRoleBackedPermissionService(
        ITeamMemberRepository teamMembers,
        IUserLookup userLookup)
    {
        ArgumentNullException.ThrowIfNull(teamMembers);
        ArgumentNullException.ThrowIfNull(userLookup);

        _teamMembers = teamMembers;
        _userLookup = userLookup;
    }

    /// <inheritdoc />
    public async Task<bool> CanViewTeamQueueAsync(
        Guid actorId,
        Guid teamId,
        CancellationToken cancellationToken)
    {
        if (await _userLookup.IsSystemAdminAsync(actorId, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        TeamMemberRole? role = await GetRoleAsync(actorId, teamId, cancellationToken).ConfigureAwait(false);

        // §5.1: any team_members.role grants view; no membership row → false.
        return role is TeamMemberRole.Manager
            or TeamMemberRole.TeamLead
            or TeamMemberRole.Member
            or TeamMemberRole.Viewer;
    }

    /// <inheritdoc />
    public async Task<bool> CanRouteTicketAsync(
        Guid actorId,
        Ticket ticket,
        Guid destinationTeamId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        if (await _userLookup.IsSystemAdminAsync(actorId, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        // §5.2: matrix is keyed on the actor's role on the source team
        // (ticket.TeamId). The destination team's role is intentionally NOT
        // consulted — the proposal forbids routing being gated by a role on
        // the receiving team. destinationTeamId is therefore captured here
        // only for parameter shape / future telemetry; it does not affect
        // the decision.
        _ = destinationTeamId;

        TeamMemberRole? role = await GetRoleAsync(actorId, ticket.TeamId, cancellationToken).ConfigureAwait(false);

        return role switch
        {
            TeamMemberRole.Manager => true,
            TeamMemberRole.TeamLead => true,
            // member: only when actor is the current assignee.
            TeamMemberRole.Member => ticket.AssigneeId == actorId,
            // viewer / non-member / null → deny.
            _ => false,
        };
    }

    /// <inheritdoc />
    public async Task<bool> CanAssignTicketAsync(
        Guid actorId,
        Ticket ticket,
        Guid targetUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        if (await _userLookup.IsSystemAdminAsync(actorId, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        TeamMemberRole? role = await GetRoleAsync(actorId, ticket.TeamId, cancellationToken).ConfigureAwait(false);

        // §5.3: role check is against the ticket's owning team.
        return role switch
        {
            // manager / team_lead can claim, re-claim, or assign anyone.
            TeamMemberRole.Manager => true,
            TeamMemberRole.TeamLead => true,
            // member: only self-assign on an unassigned ticket.
            TeamMemberRole.Member =>
                targetUserId == actorId && ticket.AssigneeId is null,
            // viewer / non-member / null → deny.
            _ => false,
        };
    }

    /// <inheritdoc />
    public async Task<bool> CanManageTeamMembersAsync(
        Guid actorId,
        Guid teamId,
        CancellationToken cancellationToken)
    {
        if (await _userLookup.IsSystemAdminAsync(actorId, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        TeamMemberRole? role = await GetRoleAsync(actorId, teamId, cancellationToken).ConfigureAwait(false);

        // Conservative derivation (proposal §5 publishes no explicit matrix for
        // this rule; reasoning documented on IPermissionService): only the two
        // leadership roles can manage membership; member / viewer / non-member
        // / null → deny.
        return role is TeamMemberRole.Manager or TeamMemberRole.TeamLead;
    }

    private async Task<TeamMemberRole?> GetRoleAsync(
        Guid userId,
        Guid teamId,
        CancellationToken cancellationToken)
    {
        TeamMember? member = await _teamMembers
            .GetAsync(userId, teamId, cancellationToken)
            .ConfigureAwait(false);
        return member?.Role;
    }
}
