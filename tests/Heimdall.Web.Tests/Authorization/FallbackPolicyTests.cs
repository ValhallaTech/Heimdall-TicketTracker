using FluentAssertions;
using Heimdall.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Heimdall.Web.Tests.Authorization;

/// <summary>
/// Verifies that <see cref="AuthorizationConfiguration.Configure"/> wires the
/// global authenticated-only fallback policy required by Phase 1 step 9 of
/// <c>docs/proposals/security-and-authorization.md</c> §9.3.
/// </summary>
public class FallbackPolicyTests
{
    [Fact]
    public void Should_RequireAuthenticatedUser_When_FallbackPolicyResolved()
    {
        var services = new ServiceCollection();
        services.AddAuthorization(AuthorizationConfiguration.Configure);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        options.FallbackPolicy.Should().NotBeNull();
        options.FallbackPolicy!.Requirements
            .Should().ContainSingle(r => r is DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public void Should_Throw_When_OptionsArgumentIsNull()
    {
        var act = () => AuthorizationConfiguration.Configure(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
