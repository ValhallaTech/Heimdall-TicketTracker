using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Models;
using Heimdall.Core.Models.Pagination;

namespace Heimdall.Core.Interfaces;

/// <summary>
/// Persistence abstraction for <see cref="Ticket"/>.
/// </summary>
public interface ITicketRepository
{
    /// <summary>Returns all tickets ordered by date created (newest first).</summary>
    Task<IReadOnlyList<Ticket>> GetAllAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Returns up to 500 tickets routed to the given <paramref name="teamId"/>, ordered
    /// newest first. Drives the per-team queue UI (Phase 2.8 step 23 of
    /// <c>docs/proposals/team-collaboration.md</c> §5.1 / §7).
    /// </summary>
    /// <param name="teamId">The team whose queue is being read.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    Task<IReadOnlyList<Ticket>> GetByTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Returns a single page of tickets that match the supplied <paramref name="query"/>,
    /// together with the total number of matching records (for pagination controls).
    /// </summary>
    /// <param name="query">Pagination, sort, and filter parameters.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// A tuple containing the page of <see cref="Ticket"/> items and the total
    /// count of records matching the filter across all pages.
    /// </returns>
    Task<(IReadOnlyList<Ticket> Items, int TotalCount)> GetPagedAsync(
        PagedQuery query,
        CancellationToken cancellationToken = default
    );

    /// <summary>Returns a single ticket or <c>null</c> if not found.</summary>
    Task<Ticket?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Creates a new ticket and returns its generated id.</summary>
    Task<int> CreateAsync(Ticket ticket, CancellationToken cancellationToken = default);

    /// <summary>Updates an existing ticket. Returns <c>true</c> if a row was affected.</summary>
    Task<bool> UpdateAsync(Ticket ticket, CancellationToken cancellationToken = default);

    /// <summary>Deletes the ticket with the given id. Returns <c>true</c> if a row was affected.</summary>
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates only the <c>team_id</c> column of the ticket (and refreshes
    /// <c>date_updated</c>). The caller supplies an open <paramref name="connection"/>
    /// and active <paramref name="transaction"/> so the audit-event INSERT for the
    /// route operation can ride alongside in the same unit of work — see
    /// <c>docs/proposals/team-collaboration.md</c> §5.4.
    /// </summary>
    /// <param name="connection">An already-open database connection. Must not be <c>null</c>.</param>
    /// <param name="transaction">An active transaction on <paramref name="connection"/>. Must not be <c>null</c>.</param>
    /// <param name="ticketId">The ticket primary key.</param>
    /// <param name="newTeamId">The team id to assign to the ticket.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns><c>true</c> if a row was updated; otherwise <c>false</c>.</returns>
    Task<bool> UpdateTeamAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        int ticketId,
        Guid newTeamId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates only the <c>assignee_id</c> column of the ticket (and refreshes
    /// <c>date_updated</c>). Pass <c>null</c> for <paramref name="newAssigneeId"/> to
    /// unassign. The caller supplies an open <paramref name="connection"/> and active
    /// <paramref name="transaction"/> so the audit-event INSERT for the assign operation
    /// can ride alongside in the same unit of work — see
    /// <c>docs/proposals/team-collaboration.md</c> §5.4.
    /// </summary>
    /// <param name="connection">An already-open database connection. Must not be <c>null</c>.</param>
    /// <param name="transaction">An active transaction on <paramref name="connection"/>. Must not be <c>null</c>.</param>
    /// <param name="ticketId">The ticket primary key.</param>
    /// <param name="newAssigneeId">The user id to assign, or <c>null</c> to unassign.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns><c>true</c> if a row was updated; otherwise <c>false</c>.</returns>
    Task<bool> UpdateAssigneeAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        int ticketId,
        Guid? newAssigneeId,
        CancellationToken cancellationToken = default);
}
