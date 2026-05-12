using System;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.Web.Authorization.Policies;
using Microsoft.AspNetCore.Authorization;

namespace Heimdall.Web.Authorization;

/// <summary>
/// Centralised authorization wiring for Heimdall. Hosts the configuration
/// delegate passed to <c>IServiceCollection.AddAuthorization(...)</c> so
/// production startup and the focused unit tests in
/// <c>Heimdall.Web.Tests.Authorization</c> apply the exact same options.
/// </summary>
/// <remarks>
/// <para>
/// Phase 1 (<c>docs/proposals/security-and-authorization.md</c> §9.3 step 9)
/// wired the global "authenticated-only" fallback policy. Phase 3.5
/// (<c>docs/proposals/openfga.md</c> §3 step 9) layered named, OpenFGA-resolved
/// resource policies on top — every relation in <c>authz/model.fga</c> has a
/// matching policy in <see cref="AuthorizationPolicies"/>. Phase 3.7 step 14
/// removes the fallback and replaces it with an explicit <see cref="AuthorizationPolicies.IsAuthenticated"/>
/// named policy applied per-page, completing full coverage.
/// </para>
/// </remarks>
public static class AuthorizationConfiguration
{
    /// <summary>
    /// Applies Heimdall's global authorization options onto the supplied
    /// <see cref="AuthorizationOptions"/> instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registers the <see cref="AuthorizationPolicies.IsAuthenticated"/> named policy
    /// that requires an authenticated user (explicit replacement for the Phase 1 fallback
    /// removed in Phase 3.7 step 14). Then registers each resource-bound named policy in
    /// <see cref="AuthorizationPolicies"/> against either an
    /// <see cref="OpenFgaRequirement"/> (resource-bound) or a
    /// <see cref="SystemAdminRequirement"/> (DB-only break-glass authority).
    /// </para>
    /// <para>
    /// Every routed Blazor page must carry an explicit <c>[Authorize]</c> attribute referencing
    /// one of the named policies defined here, or <c>[AllowAnonymous]</c> for public pages.
    /// The global fallback policy is intentionally absent — omitting an explicit attribute is a
    /// compile-detectable gap, not a silent allow.
    /// </para>
    /// </remarks>
    /// <param name="options">The options instance to mutate.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> is <c>null</c>.
    /// </exception>
    public static void Configure(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Phase 3.7 step 14 — explicit named policy replacing the Phase 1 fallback.
        // Pages that only require authentication (no resource-level FGA check) apply
        // [Authorize(Policy = AuthorizationPolicies.IsAuthenticated)] explicitly.
        options.AddPolicy(
            AuthorizationPolicies.IsAuthenticated,
            policy => policy.RequireAuthenticatedUser());

        // Phase 3.5 — named policies. Each entry maps a relation declared in
        // authz/model.fga onto an OpenFgaRequirement carrying (objectType,
        // relation, routeValueKey). The handler resolves the route value to
        // an object id at request time and forwards to OpenFGA Check().
        AddOpenFgaPolicy(
            options,
            AuthorizationPolicies.CanViewOrganization,
            TupleShapes.OrganizationType,
            "view",
            AuthorizationPolicies.OrganizationIdRouteKey);
        AddOpenFgaPolicy(
            options,
            AuthorizationPolicies.CanManageOrganizationMembers,
            TupleShapes.OrganizationType,
            "manage_members",
            AuthorizationPolicies.OrganizationIdRouteKey);

        AddOpenFgaPolicy(
            options,
            AuthorizationPolicies.CanViewTeam,
            TupleShapes.TeamType,
            "view",
            AuthorizationPolicies.TeamIdRouteKey);
        AddOpenFgaPolicy(
            options,
            AuthorizationPolicies.CanManageTeamMembers,
            TupleShapes.TeamType,
            "manage_members",
            AuthorizationPolicies.TeamIdRouteKey);

        AddOpenFgaPolicy(
            options,
            AuthorizationPolicies.CanViewProject,
            TupleShapes.ProjectType,
            "view",
            AuthorizationPolicies.ProjectIdRouteKey);
        AddOpenFgaPolicy(
            options,
            AuthorizationPolicies.CanEditProject,
            TupleShapes.ProjectType,
            "edit",
            AuthorizationPolicies.ProjectIdRouteKey);
        AddOpenFgaPolicy(
            options,
            AuthorizationPolicies.CanManageProjectMembers,
            TupleShapes.ProjectType,
            "manage_members",
            AuthorizationPolicies.ProjectIdRouteKey);

        AddOpenFgaPolicy(
            options,
            AuthorizationPolicies.CanViewTicket,
            TupleShapes.TicketType,
            "view",
            AuthorizationPolicies.TicketIdRouteKey);
        AddOpenFgaPolicy(
            options,
            AuthorizationPolicies.CanEditTicket,
            TupleShapes.TicketType,
            "edit",
            AuthorizationPolicies.TicketIdRouteKey);
        AddOpenFgaPolicy(
            options,
            AuthorizationPolicies.CanCommentTicket,
            TupleShapes.TicketType,
            "comment",
            AuthorizationPolicies.TicketIdRouteKey);
        AddOpenFgaPolicy(
            options,
            AuthorizationPolicies.CanAssignTicket,
            TupleShapes.TicketType,
            "assign",
            AuthorizationPolicies.TicketIdRouteKey);

        // System-admin policy uses the DB-only handler so it remains usable
        // during a sidecar outage (openfga.md §3 step 10).
        options.AddPolicy(
            AuthorizationPolicies.SystemAdmin,
            policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new SystemAdminRequirement()));
    }

    private static void AddOpenFgaPolicy(
        AuthorizationOptions options,
        string policyName,
        string objectType,
        string relation,
        string routeValueKey)
    {
        options.AddPolicy(
            policyName,
            policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new OpenFgaRequirement(objectType, relation, routeValueKey)));
    }
}
