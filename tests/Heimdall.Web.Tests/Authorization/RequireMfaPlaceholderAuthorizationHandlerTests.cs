using System.Security.Claims;
using FluentAssertions;
using Heimdall.Web.Authorization.Policies;
using Microsoft.AspNetCore.Authorization;

namespace Heimdall.Web.Tests.Authorization;

/// <summary>
/// Verifies the Phase 4.3 step 8 fail-closed invariant of
/// <see cref="RequireMfaPlaceholderAuthorizationHandler"/>: regardless of the
/// principal's claims, the handler must neither call
/// <see cref="AuthorizationHandlerContext.Succeed"/> nor
/// <see cref="AuthorizationHandlerContext.Fail()"/>. An unresolved requirement
/// causes the ASP.NET policy pipeline to deny — the documented "fail-closed
/// placeholder" behaviour.
/// </summary>
public class RequireMfaPlaceholderAuthorizationHandlerTests
{
    private static AuthorizationHandler<RequireMfaRequirement> CreateHandler()
    {
        // The handler is internal; instantiate via reflection so we don't
        // need an InternalsVisibleTo entry for this single test surface.
        var type = typeof(RequireMfaRequirement).Assembly
            .GetType("Heimdall.Web.Authorization.Policies.RequireMfaPlaceholderAuthorizationHandler", throwOnError: true)!;
        return (AuthorizationHandler<RequireMfaRequirement>)System.Activator.CreateInstance(type)!;
    }

    private static async Task<AuthorizationHandlerContext> InvokeAsync(ClaimsPrincipal principal)
    {
        // Arrange
        var requirement = new RequireMfaRequirement();
        var context = new AuthorizationHandlerContext(
            new[] { requirement },
            principal,
            resource: null);
        var handler = CreateHandler();

        // Act
        await handler.HandleAsync(context);
        return context;
    }

    [Fact]
    public async Task Should_NotSucceed_When_PrincipalIsAnonymous()
    {
        // Anonymous identity (no AuthenticationType set).
        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        var context = await InvokeAsync(principal);

        context.HasSucceeded.Should().BeFalse(
            "the placeholder must never grant — Phase 4.6 step 16 lands the real handler");
        context.HasFailed.Should().BeFalse(
            "the placeholder relies on absence-of-success, not explicit failure");
    }

    [Fact]
    public async Task Should_NotSucceed_When_PrincipalIsEmpty()
    {
        // Empty ClaimsPrincipal — no identities at all.
        var principal = new ClaimsPrincipal();

        var context = await InvokeAsync(principal);

        context.HasSucceeded.Should().BeFalse();
        context.HasFailed.Should().BeFalse();
    }

    [Fact]
    public async Task Should_NotSucceed_When_PrincipalIsAuthenticatedNonAdmin()
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, System.Guid.NewGuid().ToString()) },
            authenticationType: "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        var context = await InvokeAsync(principal);

        context.HasSucceeded.Should().BeFalse();
        context.HasFailed.Should().BeFalse();
    }

    [Fact]
    public async Task Should_NotSucceed_When_PrincipalIsSystemAdmin()
    {
        // Even a principal that would satisfy SystemAdmin must be denied —
        // the placeholder is unconditional.
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, System.Guid.NewGuid().ToString()),
                new Claim("system_admin", "true"),
                new Claim(ClaimTypes.Role, "system_admin"),
            },
            authenticationType: "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        var context = await InvokeAsync(principal);

        context.HasSucceeded.Should().BeFalse();
        context.HasFailed.Should().BeFalse();
    }
}
