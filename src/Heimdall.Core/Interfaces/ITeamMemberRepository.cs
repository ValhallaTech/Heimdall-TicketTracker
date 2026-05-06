using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Models;

namespace Heimdall.Core.Interfaces;

/// <summary>
/// Persistence abstraction for <see cref="TeamMember"/>. Reads and writes to the
/// <c>team_members</c> table created by <c>M202605050014_CreateTeamMembers</c>.
/// </summary>
public interface ITeamMemberRepository
{
    /// <summary>Returns all memberships for the given team, ordered by <c>AddedAt</c> ascending then <c>UserId</c> ascending.</summary>
    Task<IReadOnlyList<TeamMember>> GetByParentAsync(
        Guid parentId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Returns all team memberships held by the given user, ordered by <c>AddedAt</c> ascending then team id ascending.</summary>
    Task<IReadOnlyList<TeamMember>> GetByUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Returns the single membership identified by <c>(userId, parentId)</c>, or <c>null</c> if not found.</summary>
    Task<TeamMember?> GetAsync(
        Guid userId,
        Guid parentId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Inserts a new team membership. Duplicate composite keys surface as <c>PostgresException</c> (<c>23505</c>).</summary>
    Task AddAsync(TeamMember member, CancellationToken cancellationToken = default);

    /// <summary>Updates the <c>role</c> column of an existing membership. Returns <c>true</c> if a row was affected.</summary>
    Task<bool> UpdateRoleAsync(
        Guid userId,
        Guid parentId,
        TeamMemberRole role,
        CancellationToken cancellationToken = default
    );

    /// <summary>Deletes the membership identified by <c>(userId, parentId)</c>. Returns <c>true</c> if a row was affected.</summary>
    Task<bool> RemoveAsync(
        Guid userId,
        Guid parentId,
        CancellationToken cancellationToken = default
    );
}
