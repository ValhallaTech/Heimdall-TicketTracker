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
/// wires the global "authenticated-only" fallback policy. Phase 3.5
/// (<c>docs/proposals/openfga.md</c> §3 step 9) layers named, OpenFGA-resolved
/// resource policies on top — every relation in <c>authz/model.fga</c> has a
/// matching policy in <see cref="AuthorizationPolicies"/>. Phase 3.7 step 14
/// removes the fallback once full coverage is verified; until then both
/// stacks coexist.
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
    /// Sets a fallback policy that requires an authenticated user — every
    /// endpoint without its own authorization metadata is therefore denied to
    /// anonymous callers and explicitly-public endpoints must opt out with
    /// <c>[AllowAnonymous]</c> (login, logout, access-denied, the static
    /// error / not-found pages, the splash page). Then registers each named
    /// policy in <see cref="AuthorizationPolicies"/> against either an
    /// <see cref="OpenFgaRequirement"/> (resource-bound) or a
    /// <see cref="SystemAdminRequirement"/> (DB-only break-glass authority).
    /// </para>
    /// <para>
    /// TODO(Phase 3.7 step 14): remove the fallback policy once every Blazor
    /// page and every BLL entry point carries an explicit named policy.
    /// </para>
    /// </remarks>
    /// <param name="options">The options instance to mutate.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> is <c>null</c>.
    /// </exception>
    public static void Configure(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Phase 1 fallback — TODO(Phase 3.7 step 14): remove once every routed
        // page/endpoint carries an explicit named policy.
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();

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
