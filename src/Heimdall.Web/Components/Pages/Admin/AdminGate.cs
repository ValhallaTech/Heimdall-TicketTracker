using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Heimdall.Core.Interfaces;
using Microsoft.AspNetCore.Components.Authorization;

namespace Heimdall.Web.Components.Pages.Admin;

/// <summary>
/// Helper used by <c>/admin/*</c> pages to enforce <c>users.system_admin</c>
/// before rendering. Phase 2 ships an inline gate (this) per
/// <c>docs/proposals/team-collaboration.md</c> §7; Phase 3 will replace each
/// usage with <c>[Authorize(Policy = "SystemAdmin")]</c> once the OpenFGA policy
/// framework lands (<c>docs/proposals/openfga.md</c> step 6).
/// </summary>
internal static class AdminGate
{
    /// <summary>
    /// Returns <c>true</c> when the cascaded auth state resolves to a parseable
    /// user id and <see cref="IUserLookup.IsSystemAdminAsync"/> returns
    /// <c>true</c>; otherwise <c>false</c> (deny-closed).
    /// </summary>
    public static async Task<bool> IsSystemAdminAsync(
        Task<AuthenticationState>? authStateTask,
        IUserLookup userLookup)
    {
        if (authStateTask is null) return false;
        var state = await authStateTask;
        var raw = state.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(raw, out var id)) return false;
        return await userLookup.IsSystemAdminAsync(id);
    }
}
