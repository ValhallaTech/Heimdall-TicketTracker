using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Models;

namespace Heimdall.Core.Interfaces;

/// <summary>
/// Persistence abstraction for <see cref="Organization"/>. Reads and writes to the
/// <c>organizations</c> table created by <c>M202605050010_CreateOrganizations</c>.
/// </summary>
public interface IOrganizationRepository
{
    /// <summary>Returns all organizations ordered by slug ascending.</summary>
    Task<IReadOnlyList<Organization>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns a single organization by id, or <c>null</c> if not found.</summary>
    Task<Organization?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns a single organization by slug (case-insensitive), or <c>null</c> if not found.</summary>
    Task<Organization?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>Inserts a new organization and returns its generated id. <c>CreatedAt</c> is sourced from the database clock.</summary>
    Task<Guid> CreateAsync(Organization organization, CancellationToken cancellationToken = default);

    /// <summary>Updates the mutable columns (slug, name) of an existing organization. Returns <c>true</c> if a row was affected.</summary>
    Task<bool> UpdateAsync(Organization organization, CancellationToken cancellationToken = default);

    /// <summary>Deletes the organization with the given id. Returns <c>true</c> if a row was affected. Cascades to teams / projects.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
