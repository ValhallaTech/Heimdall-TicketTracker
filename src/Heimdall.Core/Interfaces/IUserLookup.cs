using System;
using System.Threading;
using System.Threading.Tasks;

namespace Heimdall.Core.Interfaces;

/// <summary>
/// Compact projection used by display-name resolution in the UI layer (Phase 2.8
/// step 24). Intentionally narrower than <see cref="Heimdall.Core.Models.HeimdallUser"/>
/// — only the fields safe for display are exposed; password hashes, security
/// stamps, and lockout state never leave the DAL.
/// </summary>
/// <param name="Id">The user's primary key.</param>
/// <param name="Email">
/// The user's email, also used as the human-readable display label until a
/// dedicated display-name column lands.
/// </param>
public sealed record UserSummary(Guid Id, string Email);

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

    /// <summary>
    /// Returns a compact <see cref="UserSummary"/> for the user identified by
    /// <paramref name="userId"/>, or <c>null</c> if no such user exists. Used by
    /// display-name resolution in the UI layer (Phase 2.8 step 24).
    /// </summary>
    /// <param name="userId">The user id to look up.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    Task<UserSummary?> GetByIdAsync(Guid userId, CancellationToken cancellationToken = default);
}
