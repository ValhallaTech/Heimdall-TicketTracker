using Microsoft.AspNetCore.Authorization;

namespace Heimdall.Web.Authorization.Policies;

/// <summary>
/// Authorization requirement representing "the acting user has
/// <c>users.system_admin = true</c>". Deliberately decoupled from OpenFGA so
/// it remains usable during a sidecar outage, per <c>docs/proposals/openfga.md</c>
/// §3 step 10 (DB-only break-glass authority).
/// </summary>
/// <remarks>
/// Served by <see cref="SystemAdminAuthorizationHandler"/>, which reads the flag
/// via <see cref="Heimdall.Core.Interfaces.IUserLookup.IsSystemAdminAsync"/> —
/// the same source <see cref="OpenFgaAuthorizationHandler"/> consults for the
/// break-glass override so the two handlers cannot disagree about who is and
/// is not an admin.
/// </remarks>
public sealed class SystemAdminRequirement : IAuthorizationRequirement
{
}
