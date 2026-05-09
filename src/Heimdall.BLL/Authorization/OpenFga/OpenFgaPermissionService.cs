using System;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Microsoft.Extensions.Logging;

namespace Heimdall.BLL.Authorization.OpenFga;

/// <summary>
/// Phase 3.5 implementation of <see cref="IPermissionService"/>, delivering
/// <c>docs/proposals/openfga.md</c> §3 step 9: every "may the actor do X"
/// question maps to an OpenFGA <c>Check</c> via
/// <see cref="IOpenFgaAuthorizationService"/>. Replaces
/// <c>TeamRoleBackedPermissionService</c> behind the same seam — call sites
/// are unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deny-closed.</b> Per the openfga.md §3 step 10 contract, transport / 5xx /
/// timeout failures from the sidecar map to <see langword="false"/> (handled
/// inside <see cref="OpenFgaAuthorizationService"/>). This class additionally
/// catches every non-cancellation <see cref="Exception"/> at its public
/// boundary and logs at warning, returning <see langword="false"/>, so a
/// surprise from a Phase-3.4 hook regression cannot crash a Blazor page.
/// </para>
/// <para>
/// <b>System-admin short-circuit.</b> Mirrors
/// <see cref="TeamRoleBackedPermissionService"/>: every public method consults
/// <see cref="IUserLookup.IsSystemAdminAsync"/> first so a documented operator
/// keeps full access even if the model evolves out from under them. The flag
/// remains read directly from PostgreSQL (no FGA dependency) — required so
/// the same seam is usable during a sidecar outage when paired with the
/// break-glass path in <c>SystemAdminAuthorizationHandler</c>.
/// </para>
/// <para>
/// <b>Consistency.</b> Defaults to <see cref="FgaConsistency.MinimizeLatency"/>
/// for read-your-write paths the model already covers via reporter / assignee
/// tuples (Phase 3.4 hooks). Write-after-read paths in this seam — claim,
/// route, manage-members — are user-driven retries; <c>MinimizeLatency</c> is
/// the right default and a stale deny is recoverable by a single page refresh.
/// Bumping to <see cref="FgaConsistency.HigherConsistency"/> would double our
/// p95 sidecar load for a window measured in milliseconds. Revisit if the
/// Phase 3.7 step 13 perf measurement says otherwise.
/// </para>
/// </remarks>
public sealed class OpenFgaPermissionService : IPermissionService
{
    private readonly IOpenFgaAuthorizationService _fga;
    private readonly IUserLookup _userLookup;
    private readonly ILogger<OpenFgaPermissionService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenFgaPermissionService"/> class.
    /// </summary>
    /// <param name="fga">OpenFGA <c>Check</c> seam (Phase 3.3 step 6).</param>
    /// <param name="userLookup">Sidecar-free <c>system_admin</c> lookup (Phase 1 §9.3).</param>
    /// <param name="logger">Structured logger.</param>
    /// <exception cref="ArgumentNullException">If any argument is <c>null</c>.</exception>
    public OpenFgaPermissionService(
        IOpenFgaAuthorizationService fga,
        IUserLookup userLookup,
        ILogger<OpenFgaPermissionService> logger)
    {
        ArgumentNullException.ThrowIfNull(fga);
        ArgumentNullException.ThrowIfNull(userLookup);
        ArgumentNullException.ThrowIfNull(logger);

        _fga = fga;
        _userLookup = userLookup;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> CanViewTeamQueueAsync(
        Guid actorId,
        Guid teamId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await _userLookup.IsSystemAdminAsync(actorId, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            return await CheckAsync(
                TupleShapes.UserRef(actorId),
                "view",
                TupleShapes.TeamRef(teamId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cooperative cancellation must propagate (openfga.md §3 step 6 contract).
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Deny-closed: CanViewTeamQueueAsync failed for actor {ActorId} team {TeamId}.",
                actorId,
                teamId);
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>Model-divergence note.</b> <c>team-collaboration.md</c> §5.2 keys the
    /// route decision off the actor's role on the <i>source</i> team and exempts
    /// a <c>member</c> who is the current assignee. The OpenFGA model
    /// (<c>authz/model.fga</c>) does not have a dedicated <c>route</c> relation;
    /// route is treated as an <c>assign</c>-class action because routing
    /// effectively re-targets the ticket. The model already grants <c>assign</c>
    /// to <c>reporter</c>, <c>assignee</c>, and <c>admin from parent_project</c>,
    /// so the §5.2 "member who is the current assignee" carve-out is naturally
    /// covered by the <c>assignee</c> tuple written in Phase 3.4 hooks. The
    /// "non-assignee member" rejection that §5.2 spelled out also remains
    /// (members are not project-admins). The OpenFGA expert may decide to push
    /// a dedicated <c>route</c> relation into the model in a follow-up; doing so
    /// would not change the call sites that consume this seam.
    /// </para>
    /// </remarks>
    public async Task<bool> CanRouteTicketAsync(
        Guid actorId,
        Ticket ticket,
        Guid destinationTeamId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        // destinationTeamId is intentionally not consulted — the matrix in §5.2
        // is keyed on the source team, mirrored here on the source ticket.
        _ = destinationTeamId;

        try
        {
            if (await _userLookup.IsSystemAdminAsync(actorId, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            return await CheckAsync(
                TupleShapes.UserRef(actorId),
                "assign",
                TupleShapes.TicketRef(ticket.Id),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Deny-closed: CanRouteTicketAsync failed for actor {ActorId} ticket {TicketId}.",
                actorId,
                ticket.Id);
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>Self-claim parity.</b> §5.3 lets a plain <c>member</c> on the ticket's
    /// team self-claim an unassigned ticket. The current FGA model maps
    /// <c>ticket#assign</c> to <c>reporter or assignee or admin from parent_project</c>,
    /// which does <i>not</i> include a project <c>member</c>. To preserve §5.3
    /// parity, this method short-circuits to <see langword="true"/> when
    /// <paramref name="targetUserId"/> equals <paramref name="actorId"/>, the
    /// ticket is currently unassigned, and the actor has <c>view</c> on the
    /// ticket (i.e. they are a project member or above). Flagged for the
    /// OpenFGA expert: prefer pushing a dedicated <c>self_claim</c> relation
    /// into the model so this carve-out can be deleted.
    /// </para>
    /// </remarks>
    public async Task<bool> CanAssignTicketAsync(
        Guid actorId,
        Ticket ticket,
        Guid targetUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        try
        {
            if (await _userLookup.IsSystemAdminAsync(actorId, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            string actorRef = TupleShapes.UserRef(actorId);
            string ticketRef = TupleShapes.TicketRef(ticket.Id);

            // Direct path: model already grants assign to reporter / assignee /
            // project-admin. Covers the team-collaboration §5.3 manager / team-lead
            // (collapsed to project-admin) and reclaim-by-current-assignee cases.
            bool fgaAllows = await CheckAsync(
                actorRef,
                "assign",
                ticketRef,
                cancellationToken).ConfigureAwait(false);
            if (fgaAllows)
            {
                return true;
            }

            // Self-claim parity: a plain project member self-claiming an unassigned
            // ticket. See remarks above.
            bool isSelfClaimOnUnassigned =
                targetUserId == actorId && ticket.AssigneeId is null;
            if (!isSelfClaimOnUnassigned)
            {
                return false;
            }

            return await CheckAsync(
                actorRef,
                "view",
                ticketRef,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Deny-closed: CanAssignTicketAsync failed for actor {ActorId} ticket {TicketId} target {TargetUserId}.",
                actorId,
                ticket.Id,
                targetUserId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> CanManageTeamMembersAsync(
        Guid actorId,
        Guid teamId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await _userLookup.IsSystemAdminAsync(actorId, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            return await CheckAsync(
                TupleShapes.UserRef(actorId),
                "manage_members",
                TupleShapes.TeamRef(teamId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Deny-closed: CanManageTeamMembersAsync failed for actor {ActorId} team {TeamId}.",
                actorId,
                teamId);
            return false;
        }
    }

    private Task<bool> CheckAsync(
        string user,
        string relation,
        string @object,
        CancellationToken cancellationToken)
    {
        FgaCheckRequest request = new(user, relation, @object, FgaConsistency.MinimizeLatency);
        return _fga.CheckAsync(request, cancellationToken);
    }
}
