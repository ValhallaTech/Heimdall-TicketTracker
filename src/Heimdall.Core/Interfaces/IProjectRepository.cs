using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Models;

namespace Heimdall.Core.Interfaces;

/// <summary>
/// Persistence abstraction for <see cref="Project"/>. Reads and writes to the
/// <c>projects</c> table created by <c>M202605050012_CreateProjects</c>.
/// </summary>
public interface IProjectRepository
{
    /// <summary>Returns all projects in the given team ordered by slug ascending.</summary>
    Task<IReadOnlyList<Project>> GetByTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Returns a single project by id, or <c>null</c> if not found.</summary>
    Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns a single project by parent-team id and slug (case-insensitive), or <c>null</c> if not found.</summary>
    Task<Project?> GetBySlugAsync(
        Guid teamId,
        string slug,
        CancellationToken cancellationToken = default
    );

    /// <summary>Inserts a new project and returns its generated id.</summary>
    Task<Guid> CreateAsync(Project project, CancellationToken cancellationToken = default);

    /// <summary>Updates the mutable columns (slug, name) of an existing project. Returns <c>true</c> if a row was affected.</summary>
    Task<bool> UpdateAsync(Project project, CancellationToken cancellationToken = default);

    /// <summary>Deletes the project with the given id. Returns <c>true</c> if a row was affected.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
