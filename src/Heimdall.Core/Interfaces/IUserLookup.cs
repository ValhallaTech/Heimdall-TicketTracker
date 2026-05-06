using System;
using System.Threading;
using System.Threading.Tasks;

namespace Heimdall.Core.Interfaces;

/// <summary>
/// Minimal read-only lookup used by authorization code that needs the
/// <c>users.system_admin</c> flag without taking a dependency on
/// <c>Microsoft.AspNetCore.Identity</c>'s <c>UserManager</c>. The BLL
/// (<c>TeamRoleBackedPermissionService</c>) is the primary consumer; see
/// <c>docs/proposals/team-collaboration.md</c> §3 (the system-admin
/// short-circuit lives inside the permission-service implementation, not at
/// call sites).
/// </summary>
public interface IUserLookup
{
    /// <summary>
    /// Returns <c>true</c> if the user identified by <paramref name="userId"/>
    /// exists and has <c>system_admin = true</c>; otherwise <c>false</c> (deny-closed:
    /// unknown / missing user maps to <c>false</c>, never an exception).
    /// </summary>
    /// <param name="userId">The user id to look up.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    Task<bool> IsSystemAdminAsync(Guid userId, CancellationToken cancellationToken = default);
}
