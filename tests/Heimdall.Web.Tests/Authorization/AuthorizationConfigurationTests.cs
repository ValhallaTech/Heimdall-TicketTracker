using System.Linq;
using FluentAssertions;
using Heimdall.Web.Authorization;
using Heimdall.Web.Authorization.Policies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Heimdall.Web.Tests.Authorization;

/// <summary>
/// Verifies that <see cref="AuthorizationConfiguration.Configure"/> binds every
/// named policy in <see cref="AuthorizationPolicies"/> to the right
/// <see cref="OpenFgaRequirement"/> triple — <c>(objectType, relation,
/// routeValueKey)</c> — or, for <see cref="AuthorizationPolicies.SystemAdmin"/>,
/// to the DB-only <see cref="SystemAdminRequirement"/>. Catches drift between
/// the policy constants, the OpenFGA model relations, and the policy wiring at
/// compile-test time rather than as a 403 in production.
/// </summary>
public class AuthorizationConfigurationTests
{
    private static AuthorizationOptions BuildOptions()
    {
        var services = new ServiceCollection();
        services.AddAuthorization(AuthorizationConfiguration.Configure);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
    }

    public static TheoryData<string, string, string, string> OpenFgaPolicyMatrix() => new()
    {
        // (policyName, expectedObjectType, expectedRelation, expectedRouteValueKey)
        { AuthorizationPolicies.CanViewOrganization,            "organization", "view",            AuthorizationPolicies.OrganizationIdRouteKey },
        { AuthorizationPolicies.CanManageOrganizationMembers,   "organization", "manage_members",  AuthorizationPolicies.OrganizationIdRouteKey },
        { AuthorizationPolicies.CanViewTeam,                    "team",         "view",            AuthorizationPolicies.TeamIdRouteKey },
        { AuthorizationPolicies.CanManageTeamMembers,           "team",         "manage_members",  AuthorizationPolicies.TeamIdRouteKey },
        { AuthorizationPolicies.CanViewProject,                 "project",      "view",            AuthorizationPolicies.ProjectIdRouteKey },
        { AuthorizationPolicies.CanEditProject,                 "project",      "edit",            AuthorizationPolicies.ProjectIdRouteKey },
        { AuthorizationPolicies.CanManageProjectMembers,        "project",      "manage_members",  AuthorizationPolicies.ProjectIdRouteKey },
        { AuthorizationPolicies.CanViewTicket,                  "ticket",       "view",            AuthorizationPolicies.TicketIdRouteKey },
        { AuthorizationPolicies.CanEditTicket,                  "ticket",       "edit",            AuthorizationPolicies.TicketIdRouteKey },
        { AuthorizationPolicies.CanCommentTicket,               "ticket",       "comment",         AuthorizationPolicies.TicketIdRouteKey },
        { AuthorizationPolicies.CanAssignTicket,                "ticket",       "assign",          AuthorizationPolicies.TicketIdRouteKey },
    };

    [Theory]
    [MemberData(nameof(OpenFgaPolicyMatrix))]
    public void Policy_Should_BindOpenFgaRequirement(
        string policyName,
        string expectedObjectType,
        string expectedRelation,
        string expectedRouteValueKey)
    {
        var options = BuildOptions();

        var policy = options.GetPolicy(policyName);

        policy.Should().NotBeNull($"policy '{policyName}' must be registered");
        policy!.Requirements
            .Should().ContainSingle(r => r is DenyAnonymousAuthorizationRequirement,
                "every named policy must require an authenticated user");

        var fga = policy.Requirements.OfType<OpenFgaRequirement>().Should().ContainSingle().Subject;
        fga.ObjectType.Should().Be(expectedObjectType);
        fga.Relation.Should().Be(expectedRelation);
        fga.RouteValueKey.Should().Be(expectedRouteValueKey);

        // No SystemAdminRequirement should sneak onto a resource policy — a
        // bypass would silently grant access during a sidecar outage.
        policy.Requirements.OfType<SystemAdminRequirement>().Should().BeEmpty();
    }

    [Fact]
    public void RequireMfa_Policy_Should_BindRequireMfaRequirement_AndRequireAuthenticatedUser()
    {
        // Phase 4.3 step 8 — the policy must demand authentication AND carry
        // exactly one RequireMfaRequirement so the (currently fail-closed)
        // handler is the deciding voice on every protected request.
        var options = BuildOptions();

        var policy = options.GetPolicy(AuthorizationPolicies.RequireMfa);

        policy.Should().NotBeNull();
        policy!.Requirements
            .Should().ContainSingle(r => r is DenyAnonymousAuthorizationRequirement,
                "the MFA policy must reject anonymous principals before the placeholder handler ever runs");
        policy.Requirements
            .OfType<RequireMfaRequirement>()
            .Should().HaveCount(1, "the policy is what binds the placeholder to the request");
        policy.Requirements
            .OfType<OpenFgaRequirement>()
            .Should().BeEmpty("the MFA gate is not an OpenFGA relation check");
        policy.Requirements
            .OfType<SystemAdminRequirement>()
            .Should().BeEmpty("system-admin must not bypass the MFA gate");
    }

    [Fact]
    public void SystemAdmin_Policy_Should_BindSystemAdminRequirement_AndNotOpenFga()
    {
        var options = BuildOptions();

        var policy = options.GetPolicy(AuthorizationPolicies.SystemAdmin);

        policy.Should().NotBeNull();
        policy!.Requirements
            .Should().ContainSingle(r => r is DenyAnonymousAuthorizationRequirement);
        policy.Requirements.OfType<SystemAdminRequirement>().Should().ContainSingle();
        policy.Requirements.OfType<OpenFgaRequirement>().Should().BeEmpty(
            "SystemAdmin must not consult OpenFGA — the DB-only path is the documented break-glass");
    }
}
