using System.Linq;
using FluentAssertions;
using Heimdall.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Heimdall.Web.Tests.Authorization;

/// <summary>
/// Phase 3.7 step 14 decommission verification — asserts the absence of the
/// global fallback policy and the completeness of per-page explicit
/// <c>[Authorize(Policy = ...)]</c> coverage across all routed Blazor components
/// in <c>Heimdall.Web</c>.
/// </summary>
/// <remarks>
/// <para>
/// These tests document and enforce the Phase 3.7 migration contract:
/// </para>
/// <list type="bullet">
///   <item><description>
///     The global fallback policy has been removed (replaced by explicit
///     per-page <c>[Authorize(Policy = "IsAuthenticated")]</c>).
///   </description></item>
///   <item><description>
///     Every Blazor page that carries <c>[Authorize]</c> must reference a
///     named policy — bare <c>[Authorize]</c> without a <c>Policy</c>
///     string is treated as a configuration gap because there is no fallback
///     to make it meaningful.
///   </description></item>
/// </list>
/// </remarks>
public class DecommissionVerificationTests
{
    /// <summary>
    /// Phase 3.7 step 14 removed the global fallback policy that was introduced
    /// in Phase 1 step 9. This test verifies the removal is in effect and that
    /// <see cref="AuthorizationConfiguration.Configure"/> no longer sets
    /// <see cref="AuthorizationOptions.FallbackPolicy"/>.
    /// </summary>
    [Fact]
    public void Should_HaveNullFallbackPolicy_When_Phase37Step14ConfigurationApplied()
    {
        var services = new ServiceCollection();
        services.AddAuthorization(AuthorizationConfiguration.Configure);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        options.FallbackPolicy.Should().BeNull(
            because: "Phase 3.7 step 14 removed the global fallback policy in favour of "
                + "explicit per-page [Authorize(Policy = ...)] attributes");
    }

    /// <summary>
    /// Every Blazor page that carries an <c>[AuthorizeAttribute]</c> must
    /// specify a non-empty <c>Policy</c> name. A bare <c>[Authorize]</c> without
    /// a policy falls back to nothing (the fallback was removed) and would admit
    /// all authenticated users regardless of resource permissions, which is
    /// incorrect for resource-gated pages.
    /// </summary>
    [Fact]
    public void Should_HaveExplicitPolicy_On_AllAuthorizedBlazorPages()
    {
        // Scan Heimdall.Web assembly for types with both [RouteAttribute] (Blazor
        // page directive) and [AuthorizeAttribute] (access control).
        var assembly = typeof(Program).Assembly;

        var violatingPages = assembly
            .GetTypes()
            .Where(t => t.IsDefined(typeof(RouteAttribute), inherit: false))
            .Where(t => t.IsDefined(typeof(AuthorizeAttribute), inherit: false))
            .Where(t =>
            {
                var authAttrs = t.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
                    .Cast<AuthorizeAttribute>();
                return authAttrs.Any(a => string.IsNullOrWhiteSpace(a.Policy));
            })
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        violatingPages.Should().BeEmpty(
            because: "every [Authorize] attribute on a Blazor page must reference a named policy; "
                + "bare [Authorize] without a Policy is a configuration gap now that the global "
                + "fallback policy has been removed (Phase 3.7 step 14)");
    }
}
