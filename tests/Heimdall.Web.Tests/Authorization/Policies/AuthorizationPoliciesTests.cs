using System.Linq;
using System.Reflection;
using FluentAssertions;
using Heimdall.Web.Authorization.Policies;

namespace Heimdall.Web.Tests.Authorization.Policies;

/// <summary>
/// Light-touch tests for <see cref="AuthorizationPolicies"/> — verifies every
/// public string constant is non-empty and unique so a collision cannot silently
/// rebind a policy.
/// </summary>
public class AuthorizationPoliciesTests
{
    [Fact]
    public void PolicyConstants_Should_BeNonEmpty()
    {
        var values = GetStringConstants();

        values.Should().NotBeEmpty();
        values.Should().OnlyContain(v => !string.IsNullOrWhiteSpace(v));
    }

    [Fact]
    public void PolicyConstants_Should_AllBeDistinct()
    {
        var values = GetStringConstants();

        values.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void RouteValueKeys_Should_MatchExpectedNames()
    {
        AuthorizationPolicies.OrganizationIdRouteKey.Should().Be("organizationId");
        AuthorizationPolicies.TeamIdRouteKey.Should().Be("teamId");
        AuthorizationPolicies.ProjectIdRouteKey.Should().Be("projectId");
        AuthorizationPolicies.TicketIdRouteKey.Should().Be("ticketId");
    }

    private static System.Collections.Generic.List<string> GetStringConstants() =>
        typeof(AuthorizationPolicies)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
}
