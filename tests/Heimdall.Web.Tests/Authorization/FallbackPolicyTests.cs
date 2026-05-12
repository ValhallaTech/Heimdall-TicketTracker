using FluentAssertions;
using Heimdall.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Heimdall.Web.Tests.Authorization;

/// <summary>
/// Verifies the authorization policy configuration produced by
/// <see cref="AuthorizationConfiguration.Configure"/>.
/// </summary>
/// <remarks>
/// Phase 3.7 step 14 removed the global fallback policy that was introduced in
/// Phase 1 step 9 (<c>docs/proposals/security-and-authorization.md</c> §9.3).
/// The fallback is now <see langword="null"/>; individual Blazor pages carry
/// explicit <c>[Authorize(Policy = "IsAuthenticated")]</c> attributes instead.
/// </remarks>
public class FallbackPolicyTests
{
    /// <summary>
    /// Phase 3.7 step 14 removed the fallback policy. Asserting it is null
    /// prevents any accidental re-introduction of a broad fallback that could
    /// bypass resource-level OpenFGA checks on secured pages.
    /// </summary>
    [Fact]
    public void Should_HaveNullFallbackPolicy_When_Phase37Step14ConfigurationApplied()
    {
        var services = new ServiceCollection();
        services.AddAuthorization(AuthorizationConfiguration.Configure);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        options.FallbackPolicy.Should().BeNull(
            because: "Phase 3.7 step 14 replaced the global fallback policy with explicit "
                + "[Authorize(Policy = ...)] attributes on each Blazor page");
    }

    [Fact]
    public void Should_Throw_When_OptionsArgumentIsNull()
    {
        var act = () => AuthorizationConfiguration.Configure(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
