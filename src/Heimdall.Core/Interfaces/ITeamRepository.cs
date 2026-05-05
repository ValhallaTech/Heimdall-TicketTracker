using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Models;

namespace Heimdall.Core.Interfaces;

/// <summary>
/// Persistence abstraction for <see cref="Team"/>. Reads and writes to the
/// <c>teams</c> table created by <c>M202605050011_CreateTeams</c>.
/// </summary>
public interface ITeamRepository
{
    /// <summary>Returns all teams in the given organization ordered by slug ascending.</summary>
    Task<IReadOnlyList<Team>> GetByOrganizationAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Returns a single team by id, or <c>null</c> if not found.</summary>
    Task<Team?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns a single team by parent-org id and slug (case-insensitive), or <c>null</c> if not found.</summary>
    Task<Team?> GetBySlugAsync(
        Guid organizationId,
        string slug,
        CancellationToken cancellationToken = default
    );

    /// <summary>Inserts a new team and returns its generated id.</summary>
    Task<Guid> CreateAsync(Team team, CancellationToken cancellationToken = default);

    /// <summary>Updates the mutable columns (slug, name) of an existing team. Returns <c>true</c> if a row was affected.</summary>
    Task<bool> UpdateAsync(Team team, CancellationToken cancellationToken = default);

    /// <summary>Deletes the team with the given id. Returns <c>true</c> if a row was affected. Cascades to projects.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
