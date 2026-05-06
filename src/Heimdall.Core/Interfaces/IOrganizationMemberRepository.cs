using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Models;

namespace Heimdall.Core.Interfaces;

/// <summary>
/// Persistence abstraction for <see cref="OrganizationMember"/>. Reads and writes
/// to the <c>organization_members</c> table created by
/// <c>M202605050013_CreateOrganizationMembers</c>.
/// </summary>
public interface IOrganizationMemberRepository
{
    /// <summary>Returns all memberships for the given organization, ordered by <c>AddedAt</c> ascending then <c>UserId</c> ascending.</summary>
    Task<IReadOnlyList<OrganizationMember>> GetByParentAsync(
        Guid parentId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Returns all organization memberships held by the given user, ordered by <c>AddedAt</c> ascending then organization id ascending.</summary>
    Task<IReadOnlyList<OrganizationMember>> GetByUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Returns the single membership identified by <c>(userId, parentId)</c>, or <c>null</c> if not found.</summary>
    Task<OrganizationMember?> GetAsync(
        Guid userId,
        Guid parentId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Inserts a new organization membership. Duplicate composite keys surface as <c>PostgresException</c> (<c>23505</c>).</summary>
    Task AddAsync(OrganizationMember member, CancellationToken cancellationToken = default);

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
