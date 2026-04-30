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
}
