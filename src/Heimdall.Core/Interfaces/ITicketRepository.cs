using System.Collections.Generic;
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
}
