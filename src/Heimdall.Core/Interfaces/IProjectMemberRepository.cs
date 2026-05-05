using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Models;

namespace Heimdall.Core.Interfaces;

/// <summary>
/// Persistence abstraction for <see cref="ProjectMember"/>. Reads and writes to the
/// <c>project_members</c> table created by <c>M202605050015_CreateProjectMembers</c>.
/// </summary>
public interface IProjectMemberRepository
{
    /// <summary>Returns all memberships for the given project, ordered by <c>AddedAt</c> ascending then <c>UserId</c> ascending.</summary>
    Task<IReadOnlyList<ProjectMember>> GetByParentAsync(
        Guid parentId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Returns all project memberships held by the given user, ordered by <c>AddedAt</c> ascending then project id ascending.</summary>
    Task<IReadOnlyList<ProjectMember>> GetByUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Returns the single membership identified by <c>(userId, parentId)</c>, or <c>null</c> if not found.</summary>
    Task<ProjectMember?> GetAsync(
        Guid userId,
        Guid parentId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Inserts a new project membership. Duplicate composite keys surface as <c>PostgresException</c> (<c>23505</c>).</summary>
    Task AddAsync(ProjectMember member, CancellationToken cancellationToken = default);

    /// <summary>Updates the <c>role</c> column of an existing membership. Returns <c>true</c> if a row was affected.</summary>
    Task<bool> UpdateRoleAsync(
        Guid userId,
        Guid parentId,
        string role,
        CancellationToken cancellationToken = default
    );

    /// <summary>Deletes the membership identified by <c>(userId, parentId)</c>. Returns <c>true</c> if a row was affected.</summary>
    Task<bool> RemoveAsync(
        Guid userId,
        Guid parentId,
        CancellationToken cancellationToken = default
    );
}
