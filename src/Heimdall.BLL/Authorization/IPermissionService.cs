using System;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Models;

namespace Heimdall.BLL.Authorization;

/// <summary>
/// Single seam for every authorization check defined in
/// <c>docs/proposals/team-collaboration.md</c> §5. Phase 2 ships
/// <see cref="TeamRoleBackedPermissionService"/> (reads <c>team_members.role</c>
/// and <c>users.system_admin</c>); Phase 3 (<c>docs/proposals/openfga.md</c> step 6)
/// swaps in an <c>OpenFgaPermissionService</c> behind the same interface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deny-closed by default.</b> Per §6, any unrecognised role, unknown user, or
/// missing membership row maps to <see langword="false"/>; implementations must
/// never throw on an authorization-data miss. <c>system_admin == true</c> is the
/// only short-circuit and lives inside the implementation — call sites never read
/// the flag directly.
/// </para>
/// <para>
/// Cutover discipline: no call site outside this interface reads
/// <c>team_members.role</c> for authorization decisions. Repositories may still
/// project the column for read-only views (e.g. the admin panel), but every
/// "may the actor do X" question goes through this seam.
/// </para>
/// </remarks>
public interface IPermissionService
{
    /// <summary>
    /// May the actor view the queue (the list of tickets routed to / owned by) the
    /// supplied team? Implements <c>docs/proposals/team-collaboration.md</c> §5.1:
    /// <c>system_admin</c> short-circuits to <c>true</c> for any team; otherwise the
    /// actor must hold any of <c>manager</c> / <c>team_lead</c> / <c>member</c> /
    /// <c>viewer</c> on the target team.
    /// </summary>
    /// <param name="actorId">The acting user's id.</param>
    /// <param name="teamId">The team whose queue is being viewed.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    Task<bool> CanViewTeamQueueAsync(Guid actorId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// May the actor route <paramref name="ticket"/> from its current team
    /// (<c>ticket.TeamId</c>) to <paramref name="destinationTeamId"/>? Implements
    /// <c>docs/proposals/team-collaboration.md</c> §5.2: the destination team's role
    /// is <b>not</b> consulted; the matrix is keyed on the actor's role on the source
    /// team. <c>system_admin</c> → <c>true</c>; <c>manager</c> / <c>team_lead</c> →
    /// <c>true</c>; <c>member</c> → <c>true</c> only when the actor is the current
    /// assignee (<c>ticket.AssigneeId == actorId</c>); <c>viewer</c> / non-member →
    /// <c>false</c>.
    /// </summary>
    /// <param name="actorId">The acting user's id.</param>
    /// <param name="ticket">The ticket being routed; <c>ticket.TeamId</c> is the source team.</param>
    /// <param name="destinationTeamId">The team the ticket is being routed to.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    Task<bool> CanRouteTicketAsync(Guid actorId, Ticket ticket, Guid destinationTeamId, CancellationToken cancellationToken);

    /// <summary>
    /// May the actor assign <paramref name="ticket"/> to <paramref name="targetUserId"/>?
    /// Implements <c>docs/proposals/team-collaboration.md</c> §5.3 with self-assignment
    /// defined as <c>targetUserId == actorId</c>. The role check is against
    /// <c>ticket.TeamId</c> (the ticket's owning team). <c>system_admin</c> → <c>true</c>;
    /// <c>manager</c> / <c>team_lead</c> → <c>true</c> (claim or steal); <c>member</c> →
    /// <c>true</c> only when self-assigning (<c>targetUserId == actorId</c>) <b>and</b>
    /// the ticket is currently unassigned (<c>ticket.AssigneeId is null</c>);
    /// <c>viewer</c> / non-member → <c>false</c>.
    /// </summary>
    /// <param name="actorId">The acting user's id.</param>
    /// <param name="ticket">The ticket being assigned.</param>
    /// <param name="targetUserId">The user the ticket would be assigned to.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    Task<bool> CanAssignTicketAsync(Guid actorId, Ticket ticket, Guid targetUserId, CancellationToken cancellationToken);

    /// <summary>
    /// May the actor manage the membership roster of <paramref name="teamId"/> (add /
    /// remove members, change roles)? <c>docs/proposals/team-collaboration.md</c> §5
    /// does not publish a dedicated matrix for this rule; the conservative
    /// derivation applied by <see cref="TeamRoleBackedPermissionService"/> is:
    /// <c>system_admin</c> → <c>true</c>; <c>manager</c> / <c>team_lead</c> →
    /// <c>true</c>; <c>member</c> / <c>viewer</c> / non-member → <c>false</c>. This
    /// derivation is documented here so future review can challenge it without
    /// having to reverse-engineer the implementation.
    /// </summary>
    /// <param name="actorId">The acting user's id.</param>
    /// <param name="teamId">The team whose membership is being managed.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    Task<bool> CanManageTeamMembersAsync(Guid actorId, Guid teamId, CancellationToken cancellationToken);
}
