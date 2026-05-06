using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Dtos;
using Heimdall.Core.Models.Pagination;

namespace Heimdall.Core.Interfaces;

/// <summary>
/// Application-facing CRUD service for tickets, exposing DTOs to the presentation layer.
/// </summary>
public interface ITicketService
{
    /// <summary>Lists all tickets (may be served from cache).</summary>
    Task<IReadOnlyList<TicketDto>> GetAllAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Returns a single page of tickets matching the supplied <paramref name="query"/>,
    /// with total count metadata for pagination controls.
    /// Results are not cached due to the high cardinality of possible permutations.
    /// </summary>
    /// <param name="query">Pagination, sort, and filter parameters.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A <see cref="PagedResult{T}"/> containing the page items and paging metadata.</returns>
    Task<PagedResult<TicketDto>> GetPagedAsync(
        PagedQuery query,
        CancellationToken cancellationToken = default
    );

    /// <summary>Loads a single ticket.</summary>
    Task<TicketDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Creates a new ticket and returns the persisted DTO (with generated id/timestamps).</summary>
    Task<TicketDto> CreateAsync(
        TicketDto dto,
        CancellationToken cancellationToken = default
    );

    /// <summary>Updates an existing ticket.</summary>
    Task<bool> UpdateAsync(TicketDto dto, CancellationToken cancellationToken = default);

    /// <summary>Deletes a ticket by id.</summary>
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Routes <paramref name="ticketId"/> to <paramref name="destinationTeamId"/> on
    /// behalf of <paramref name="actorId"/>. Implements
    /// <c>docs/proposals/team-collaboration.md</c> §5.2 / §5.4: the permission gate is
    /// <see cref="Heimdall.BLL.Authorization.IPermissionService.CanRouteTicketAsync"/>;
    /// the <c>tickets.team_id</c> UPDATE and the <c>ticket_routed</c>
    /// <see cref="Heimdall.Core.Auditing.AuditEvent"/> INSERT are committed atomically
    /// in a single transaction.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> (no audit row written) when the ticket id does
    /// not exist, when the row vanished between the load and the UPDATE, or when the
    /// ticket is already on <paramref name="destinationTeamId"/> (idempotent no-op —
    /// keeps the audit feed signal-clean).
    /// </remarks>
    /// <param name="actorId">The acting user's id.</param>
    /// <param name="ticketId">The ticket primary key.</param>
    /// <param name="destinationTeamId">The team the ticket is being routed to.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns><see langword="true"/> if the route was applied; otherwise <see langword="false"/>.</returns>
    /// <exception cref="UnauthorizedAccessException">
    /// Thrown when the actor is not permitted to route the ticket per
    /// <c>docs/proposals/team-collaboration.md</c> §5.2.
    /// </exception>
    Task<bool> RouteTicketAsync(
        Guid actorId,
        int ticketId,
        Guid destinationTeamId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Self-assigns <paramref name="ticketId"/> to <paramref name="actorId"/> (a "claim").
    /// Implements <c>docs/proposals/team-collaboration.md</c> §5.3 / §5.4 with the
    /// permission gate keyed on self-assignment
    /// (<see cref="Heimdall.BLL.Authorization.IPermissionService.CanAssignTicketAsync"/>
    /// with <c>targetUserId == actorId</c>); the <c>tickets.assignee_id</c> UPDATE and
    /// the <c>ticket_assigned</c> audit row are committed atomically.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> (no audit row) when the ticket does not exist,
    /// when the row vanished mid-flight, or when the ticket is already assigned to the
    /// actor (idempotent no-op).
    /// </remarks>
    /// <param name="actorId">The acting user's id; also the new assignee.</param>
    /// <param name="ticketId">The ticket primary key.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns><see langword="true"/> if the claim was applied; otherwise <see langword="false"/>.</returns>
    /// <exception cref="UnauthorizedAccessException">
    /// Thrown when the actor is not permitted to claim the ticket.
    /// </exception>
    Task<bool> ClaimTicketAsync(
        Guid actorId,
        int ticketId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Assigns <paramref name="ticketId"/> to <paramref name="targetUserId"/> on behalf
    /// of <paramref name="actorId"/>. Implements
    /// <c>docs/proposals/team-collaboration.md</c> §5.3 / §5.4. The permission gate is
    /// <see cref="Heimdall.BLL.Authorization.IPermissionService.CanAssignTicketAsync"/>;
    /// the <c>tickets.assignee_id</c> UPDATE and the <c>ticket_assigned</c> audit row
    /// are committed atomically. Pass <c>actorId == targetUserId</c> to self-assign
    /// (equivalent to <see cref="ClaimTicketAsync"/>).
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> (no audit row) when the ticket does not exist,
    /// when the row vanished mid-flight, or when the ticket is already assigned to
    /// <paramref name="targetUserId"/> (idempotent no-op).
    /// </remarks>
    /// <param name="actorId">The acting user's id.</param>
    /// <param name="ticketId">The ticket primary key.</param>
    /// <param name="targetUserId">The user the ticket is being assigned to.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns><see langword="true"/> if the assignment was applied; otherwise <see langword="false"/>.</returns>
    /// <exception cref="UnauthorizedAccessException">
    /// Thrown when the actor is not permitted to assign the ticket per
    /// <c>docs/proposals/team-collaboration.md</c> §5.3.
    /// </exception>
    Task<bool> AssignTicketAsync(
        Guid actorId,
        int ticketId,
        Guid targetUserId,
        CancellationToken cancellationToken = default
    );
}
