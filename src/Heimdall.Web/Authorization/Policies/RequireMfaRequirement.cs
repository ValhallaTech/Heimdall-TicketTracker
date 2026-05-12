using Microsoft.AspNetCore.Authorization;

namespace Heimdall.Web.Authorization.Policies;

/// <summary>
/// Authorization requirement representing "the acting user has satisfied the
/// Heimdall MFA gate" — i.e. is either not a privileged actor subject to MFA at
/// all, or is a privileged actor whose current session is authenticated with a
/// second factor and whose <c>users.two_factor_enabled</c> column is <c>true</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Phase 4.3 step 8 placeholder.</strong> The real handler is wired in
/// Phase 4.6 step 16 (<c>docs/implementation/phase-4-checklist.md</c>), which
/// consults OpenFGA + <c>amr</c> claims + the seed-organization admin set to
/// decide whether to grant. Until that handler exists, the requirement is
/// served by <see cref="RequireMfaPlaceholderAuthorizationHandler"/>, which
/// is a fail-closed no-op: any page that opts into
/// <see cref="AuthorizationPolicies.RequireMfa"/> before step 16 lands will
/// 403 rather than silently allow.
/// </para>
/// </remarks>
public sealed class RequireMfaRequirement : IAuthorizationRequirement
{
}
