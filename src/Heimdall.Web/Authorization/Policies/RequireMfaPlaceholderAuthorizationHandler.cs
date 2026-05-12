using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;

namespace Heimdall.Web.Authorization.Policies;

/// <summary>
/// Fail-closed placeholder handler for <see cref="RequireMfaRequirement"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Phase 4.3 step 8 placeholder.</strong> The real handler — which
/// resolves whether the current actor is an organization admin via OpenFGA,
/// inspects the <c>amr</c> claim, and reads <c>users.two_factor_enabled</c> —
/// lands in Phase 4.6 step 16. Until then, this placeholder neither calls
/// <see cref="AuthorizationHandlerContext.Succeed"/> nor
/// <see cref="AuthorizationHandlerContext.Fail()"/>: an unresolved
/// requirement causes the ASP.NET policy pipeline to treat the overall
/// authorization decision as failed, so any page that opts into
/// <see cref="AuthorizationPolicies.RequireMfa"/> before step 16 will return
/// 403. This is the fail-closed invariant called out in the checklist —
/// defence-in-depth against accidental early adoption of the policy.
/// </para>
/// </remarks>
internal sealed class RequireMfaPlaceholderAuthorizationHandler
    : AuthorizationHandler<RequireMfaRequirement>
{
    /// <summary>
    /// Intentional no-op. See class-level remarks for the fail-closed rationale.
    /// </summary>
    /// <param name="context">The authorization context (ignored).</param>
    /// <param name="requirement">The requirement being evaluated (ignored).</param>
    /// <returns>A completed task.</returns>
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        RequireMfaRequirement requirement)
    {
        // Intentionally neither Succeed nor Fail — the unresolved requirement
        // is what makes the placeholder fail closed. See class-level remarks.
        return Task.CompletedTask;
    }
}
