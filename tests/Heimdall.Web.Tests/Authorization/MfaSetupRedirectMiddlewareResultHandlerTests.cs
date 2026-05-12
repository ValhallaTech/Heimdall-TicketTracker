using System.Threading.Tasks;
using FluentAssertions;
using Heimdall.Web.Authorization;
using Heimdall.Web.Authorization.Policies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Heimdall.Web.Tests.Authorization;

/// <summary>
/// Unit tests for <see cref="MfaSetupRedirectMiddlewareResultHandler"/>. Pins
/// the Phase 4.6 step 17 contract: only <see cref="RequireMfaRequirement"/>
/// failures redirect; only GET/HEAD redirect; never redirect when already on
/// <c>/account/mfa</c> (anti-loop); all other outcomes flow to the default
/// handler.
/// </summary>
public class MfaSetupRedirectMiddlewareResultHandlerTests
{
    private static MfaSetupRedirectMiddlewareResultHandler CreateSut() =>
        new(NullLogger<MfaSetupRedirectMiddlewareResultHandler>.Instance);

    private static AuthorizationPolicy DummyPolicy() =>
        new AuthorizationPolicyBuilder().AddRequirements(new RequireMfaRequirement()).Build();

    private static HttpContext CreateContext(string method = "GET", string path = "/admin/audit")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;

        // The default IAuthorizationMiddlewareResultHandler calls
        // HttpContext.ForbidAsync() when the policy denies, which resolves
        // IAuthenticationService from RequestServices. Provide a no-op double
        // so the non-redirect branches under test don't blow up on DI.
        var sc = new ServiceCollection();
        var authService = new Mock<IAuthenticationService>();
        authService.Setup(s => s.ForbidAsync(It.IsAny<HttpContext>(), It.IsAny<string?>(), It.IsAny<AuthenticationProperties?>()))
                   .Returns(Task.CompletedTask);
        authService.Setup(s => s.ChallengeAsync(It.IsAny<HttpContext>(), It.IsAny<string?>(), It.IsAny<AuthenticationProperties?>()))
                   .Returns(Task.CompletedTask);
        sc.AddSingleton(authService.Object);
        ctx.RequestServices = sc.BuildServiceProvider();

        return ctx;
    }

    private static PolicyAuthorizationResult RequireMfaFailure()
    {
        var failure = AuthorizationFailure.Failed(new[] { (IAuthorizationRequirement)new RequireMfaRequirement() });
        return PolicyAuthorizationResult.Forbid(failure);
    }

    private static PolicyAuthorizationResult OtherFailure()
    {
        var failure = AuthorizationFailure.Failed(new[] { (IAuthorizationRequirement)new SystemAdminRequirement() });
        return PolicyAuthorizationResult.Forbid(failure);
    }

    [Fact]
    public async Task Should_Redirect_When_GetFailsOnRequireMfa()
    {
        var ctx = CreateContext();
        var sut = CreateSut();
        bool nextInvoked = false;

        await sut.HandleAsync(_ => { nextInvoked = true; return Task.CompletedTask; }, ctx, DummyPolicy(), RequireMfaFailure());

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status302Found);
        ctx.Response.Headers.Location.ToString().Should().Be(MfaSetupRedirectMiddlewareResultHandler.MfaSetupPath);
        nextInvoked.Should().BeFalse();
    }

    [Fact]
    public async Task Should_Redirect_When_HeadFailsOnRequireMfa()
    {
        var ctx = CreateContext(method: "HEAD");
        var sut = CreateSut();

        await sut.HandleAsync(_ => Task.CompletedTask, ctx, DummyPolicy(), RequireMfaFailure());

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status302Found);
    }

    [Fact]
    public async Task Should_NotRedirect_When_PostFailsOnRequireMfa()
    {
        var ctx = CreateContext(method: "POST");
        var sut = CreateSut();

        await sut.HandleAsync(_ => Task.CompletedTask, ctx, DummyPolicy(), RequireMfaFailure());

        ctx.Response.Headers.Location.ToString().Should().BeEmpty();
        // Default handler emits 403 for an unauthenticated/forbidden anonymous user.
        ctx.Response.StatusCode.Should().NotBe(StatusCodes.Status302Found);
    }

    [Fact]
    public async Task Should_NotRedirect_When_AlreadyUnderMfaPath()
    {
        var ctx = CreateContext(path: "/account/mfa/setup");
        var sut = CreateSut();

        await sut.HandleAsync(_ => Task.CompletedTask, ctx, DummyPolicy(), RequireMfaFailure());

        ctx.Response.Headers.Location.ToString().Should().BeEmpty();
        ctx.Response.StatusCode.Should().NotBe(StatusCodes.Status302Found);
    }

    [Fact]
    public async Task Should_NotRedirect_When_FailedOnDifferentRequirement()
    {
        var ctx = CreateContext();
        var sut = CreateSut();

        await sut.HandleAsync(_ => Task.CompletedTask, ctx, DummyPolicy(), OtherFailure());

        ctx.Response.Headers.Location.ToString().Should().BeEmpty();
        ctx.Response.StatusCode.Should().NotBe(StatusCodes.Status302Found);
    }

    [Fact]
    public async Task Should_DelegateToNext_When_AuthorizationSucceeded()
    {
        var ctx = CreateContext();
        var sut = CreateSut();
        bool nextInvoked = false;

        await sut.HandleAsync(_ => { nextInvoked = true; return Task.CompletedTask; }, ctx, DummyPolicy(), PolicyAuthorizationResult.Success());

        nextInvoked.Should().BeTrue();
        ctx.Response.Headers.Location.ToString().Should().BeEmpty();
    }
}
