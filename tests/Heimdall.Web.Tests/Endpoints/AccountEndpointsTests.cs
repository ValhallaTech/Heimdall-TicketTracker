using System.Security.Claims;
using FluentAssertions;
using Heimdall.Core.Auditing;
using Heimdall.Core.Models;
using Heimdall.Web.Endpoints;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Heimdall.Web.Tests.Endpoints;

/// <summary>
/// Unit tests for <see cref="AccountEndpoints.HandleLoginAsync"/> and
/// <see cref="AccountEndpoints.HandleLogoutAsync"/>. The handlers are static
/// methods so we can drive them directly with a <see cref="DefaultHttpContext"/>
/// and Moq doubles for <see cref="UserManager{TUser}"/>, <see cref="SignInManager{TUser}"/>,
/// and <see cref="IAuditEventWriter"/> — no <c>WebApplicationFactory</c> needed.
/// </summary>
public class AccountEndpointsTests
{
    private readonly Mock<IAuditEventWriter> _audit = new();

    private static Mock<UserManager<HeimdallUser>> CreateUserManagerMock()
    {
        var store = new Mock<IUserStore<HeimdallUser>>();
        return new Mock<UserManager<HeimdallUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
    }

    private static Mock<SignInManager<HeimdallUser>> CreateSignInManagerMock(UserManager<HeimdallUser> userManager)
    {
        var contextAccessor = new Mock<IHttpContextAccessor>();
        var claimsFactory = new Mock<IUserClaimsPrincipalFactory<HeimdallUser>>();
        return new Mock<SignInManager<HeimdallUser>>(
            userManager,
            contextAccessor.Object,
            claimsFactory.Object,
            Options.Create(new IdentityOptions()),
            NullLogger<SignInManager<HeimdallUser>>.Instance,
            new Mock<IAuthenticationSchemeProvider>().Object,
            new Mock<IUserConfirmation<HeimdallUser>>().Object);
    }

    private static HttpContext CreateHttpContext(ClaimsPrincipal? user = null)
    {
        var ctx = new DefaultHttpContext();
        var sc = new ServiceCollection();
        sc.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        ctx.RequestServices = sc.BuildServiceProvider();
        if (user is not null)
        {
            ctx.User = user;
        }

        ctx.Request.Headers.UserAgent = "test-agent";
        return ctx;
    }

    private static HeimdallUser SampleUser() => new()
    {
        Id = Guid.NewGuid(),
        Email = "user@example.com",
        NormalizedEmail = "USER@EXAMPLE.COM",
        SecurityStamp = "stamp",
        ConcurrencyStamp = "concur",
    };

    // -----------------------------------------------------------------------------
    // Login — success / failure / lockout / not-allowed
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task Should_RedirectToHome_When_LoginSucceedsWithValidCredentials()
    {
        var ctx = CreateHttpContext();
        var user = SampleUser();
        var um = CreateUserManagerMock();
        um.Setup(x => x.FindByEmailAsync(user.Email)).ReturnsAsync(user);
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.PasswordSignInAsync(user, "pw", false, true))
          .ReturnsAsync(SignInResult.Success);

        var result = await AccountEndpoints.HandleLoginAsync(
            ctx, user.Email, "pw", returnUrl: null,
            um.Object, sm.Object, _audit.Object, default);

        var redirect = result.Should().BeOfType<RedirectHttpResult>().Subject;
        redirect.Url.Should().Be("/");
    }

    [Fact]
    public async Task Should_RedirectToLoginWithError_When_LoginFailsWithBadPassword()
    {
        var ctx = CreateHttpContext();
        var user = SampleUser();
        var um = CreateUserManagerMock();
        um.Setup(x => x.FindByEmailAsync(user.Email)).ReturnsAsync(user);
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.PasswordSignInAsync(user, "bad", false, true))
          .ReturnsAsync(SignInResult.Failed);

        var result = await AccountEndpoints.HandleLoginAsync(
            ctx, user.Email, "bad", null, um.Object, sm.Object, _audit.Object, default);

        var redirect = result.Should().BeOfType<RedirectHttpResult>().Subject;
        redirect.Url.Should().Be("/login?error=invalid-credentials");
    }

    [Fact]
    public async Task Should_RedirectToLoginWithError_When_LoginFailsWithUnknownUser()
    {
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();
        um.Setup(x => x.FindByEmailAsync(It.IsAny<string>())).ReturnsAsync((HeimdallUser?)null);
        var sm = CreateSignInManagerMock(um.Object);

        var result = await AccountEndpoints.HandleLoginAsync(
            ctx, "ghost@example.com", "pw", null, um.Object, sm.Object, _audit.Object, default);

        var redirect = result.Should().BeOfType<RedirectHttpResult>().Subject;
        redirect.Url.Should().Be("/login?error=invalid-credentials");
        sm.Verify(
            x => x.PasswordSignInAsync(It.IsAny<HeimdallUser>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task Should_RespectReturnUrl_When_LoginSucceedsAndReturnUrlIsLocal()
    {
        var ctx = CreateHttpContext();
        var user = SampleUser();
        var um = CreateUserManagerMock();
        um.Setup(x => x.FindByEmailAsync(user.Email)).ReturnsAsync(user);
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.PasswordSignInAsync(user, "pw", false, true))
          .ReturnsAsync(SignInResult.Success);

        var result = await AccountEndpoints.HandleLoginAsync(
            ctx, user.Email, "pw", "/tickets", um.Object, sm.Object, _audit.Object, default);

        var redirect = result.Should().BeOfType<RedirectHttpResult>().Subject;
        redirect.Url.Should().Be("/tickets");
    }

    [Fact]
    public async Task Should_IgnoreReturnUrl_When_ReturnUrlIsExternalUrl()
    {
        var ctx = CreateHttpContext();
        var user = SampleUser();
        var um = CreateUserManagerMock();
        um.Setup(x => x.FindByEmailAsync(user.Email)).ReturnsAsync(user);
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.PasswordSignInAsync(user, "pw", false, true))
          .ReturnsAsync(SignInResult.Success);

        var result = await AccountEndpoints.HandleLoginAsync(
            ctx, user.Email, "pw", "https://evil.example.com",
            um.Object, sm.Object, _audit.Object, default);

        var redirect = result.Should().BeOfType<RedirectHttpResult>().Subject;
        redirect.Url.Should().Be("/");
    }

    [Fact]
    public async Task Should_IgnoreReturnUrl_When_ReturnUrlIsProtocolRelative()
    {
        var ctx = CreateHttpContext();
        var user = SampleUser();
        var um = CreateUserManagerMock();
        um.Setup(x => x.FindByEmailAsync(user.Email)).ReturnsAsync(user);
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.PasswordSignInAsync(user, "pw", false, true))
          .ReturnsAsync(SignInResult.Success);

        var result = await AccountEndpoints.HandleLoginAsync(
            ctx, user.Email, "pw", "//evil.example.com",
            um.Object, sm.Object, _audit.Object, default);

        var redirect = result.Should().BeOfType<RedirectHttpResult>().Subject;
        redirect.Url.Should().Be("/");
    }

    [Fact]
    public async Task Should_IgnoreReturnUrl_When_ReturnUrlIsBackslashPrefixed()
    {
        var ctx = CreateHttpContext();
        var user = SampleUser();
        var um = CreateUserManagerMock();
        um.Setup(x => x.FindByEmailAsync(user.Email)).ReturnsAsync(user);
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.PasswordSignInAsync(user, "pw", false, true))
          .ReturnsAsync(SignInResult.Success);

        var result = await AccountEndpoints.HandleLoginAsync(
            ctx, user.Email, "pw", "/\\evil.example.com",
            um.Object, sm.Object, _audit.Object, default);

        var redirect = result.Should().BeOfType<RedirectHttpResult>().Subject;
        redirect.Url.Should().Be("/");
    }

    // -----------------------------------------------------------------------------
    // Audit emission
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task Should_WriteAuditEvent_When_LoginSucceeds()
    {
        var ctx = CreateHttpContext();
        var user = SampleUser();
        var um = CreateUserManagerMock();
        um.Setup(x => x.FindByEmailAsync(user.Email)).ReturnsAsync(user);
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.PasswordSignInAsync(user, "pw", false, true))
          .ReturnsAsync(SignInResult.Success);

        await AccountEndpoints.HandleLoginAsync(
            ctx, user.Email, "pw", null, um.Object, sm.Object, _audit.Object, default);

        _audit.Verify(
            x => x.WriteAsync(It.Is<AuditEvent>(e => e.EventType == "login.success" && e.ActorUserId == user.Id),
                              It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_WriteAuditEvent_When_LoginFailsWithBadPassword()
    {
        var ctx = CreateHttpContext();
        var user = SampleUser();
        var um = CreateUserManagerMock();
        um.Setup(x => x.FindByEmailAsync(user.Email)).ReturnsAsync(user);
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.PasswordSignInAsync(user, "bad", false, true))
          .ReturnsAsync(SignInResult.Failed);

        await AccountEndpoints.HandleLoginAsync(
            ctx, user.Email, "bad", null, um.Object, sm.Object, _audit.Object, default);

        _audit.Verify(
            x => x.WriteAsync(It.Is<AuditEvent>(e => e.EventType == "login.failure.bad_password"
                                                     && e.ActorUserId == user.Id),
                              It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_WriteAuditEvent_When_LoginFailsWithUnknownUser()
    {
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();
        um.Setup(x => x.FindByEmailAsync(It.IsAny<string>())).ReturnsAsync((HeimdallUser?)null);
        var sm = CreateSignInManagerMock(um.Object);

        await AccountEndpoints.HandleLoginAsync(
            ctx, "ghost@example.com", "pw", null, um.Object, sm.Object, _audit.Object, default);

        _audit.Verify(
            x => x.WriteAsync(It.Is<AuditEvent>(e => e.EventType == "login.failure.unknown_user"
                                                     && e.ActorUserId == null
                                                     && e.PayloadJson.Contains("example.com")),
                              It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_WriteAuditEvent_When_LoginIsLockedOut()
    {
        var ctx = CreateHttpContext();
        var user = SampleUser();
        var um = CreateUserManagerMock();
        um.Setup(x => x.FindByEmailAsync(user.Email)).ReturnsAsync(user);
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.PasswordSignInAsync(user, "pw", false, true))
          .ReturnsAsync(SignInResult.LockedOut);

        await AccountEndpoints.HandleLoginAsync(
            ctx, user.Email, "pw", null, um.Object, sm.Object, _audit.Object, default);

        _audit.Verify(
            x => x.WriteAsync(It.Is<AuditEvent>(e => e.EventType == "login.lockout"),
                              It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_WriteAuditEvent_When_LoginIsNotAllowed()
    {
        var ctx = CreateHttpContext();
        var user = SampleUser();
        var um = CreateUserManagerMock();
        um.Setup(x => x.FindByEmailAsync(user.Email)).ReturnsAsync(user);
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.PasswordSignInAsync(user, "pw", false, true))
          .ReturnsAsync(SignInResult.NotAllowed);

        await AccountEndpoints.HandleLoginAsync(
            ctx, user.Email, "pw", null, um.Object, sm.Object, _audit.Object, default);

        _audit.Verify(
            x => x.WriteAsync(It.Is<AuditEvent>(e => e.EventType == "login.not_allowed"),
                              It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_NotThrow_When_AuditWriteFails()
    {
        var ctx = CreateHttpContext();
        var user = SampleUser();
        var um = CreateUserManagerMock();
        um.Setup(x => x.FindByEmailAsync(user.Email)).ReturnsAsync(user);
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.PasswordSignInAsync(user, "pw", false, true))
          .ReturnsAsync(SignInResult.Success);
        _audit.Setup(x => x.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("db down"));

        var result = await AccountEndpoints.HandleLoginAsync(
            ctx, user.Email, "pw", null, um.Object, sm.Object, _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>();
    }

    // -----------------------------------------------------------------------------
    // Logout
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task Should_RedirectToHome_When_LogoutCalledWhileAuthenticated()
    {
        var userId = Guid.NewGuid();
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
        }, authenticationType: "Cookies");
        var principal = new ClaimsPrincipal(identity);
        var ctx = CreateHttpContext(principal);
        var um = CreateUserManagerMock();
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.SignOutAsync()).Returns(Task.CompletedTask).Verifiable();

        var result = await AccountEndpoints.HandleLogoutAsync(ctx, sm.Object, _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().Be("/");
        sm.Verify();
        _audit.Verify(
            x => x.WriteAsync(It.Is<AuditEvent>(e => e.EventType == "logout.success"
                                                     && e.ActorUserId == userId),
                              It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_RedirectToHome_When_LogoutCalledAnonymous()
    {
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.SignOutAsync()).Returns(Task.CompletedTask);

        var result = await AccountEndpoints.HandleLogoutAsync(ctx, sm.Object, _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().Be("/");
        _audit.Verify(
            x => x.WriteAsync(It.Is<AuditEvent>(e => e.EventType == "logout.anonymous"
                                                     && e.ActorUserId == null),
                              It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // -----------------------------------------------------------------------------
    // MapAccountEndpoints — defensive arg checks
    // -----------------------------------------------------------------------------

    [Fact]
    public void Should_Throw_When_MapAccountEndpointsCalledWithNullBuilder()
    {
        Action act = () => AccountEndpoints.MapAccountEndpoints(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_RegisterLoginAndLogoutRoutes_When_MapAccountEndpointsCalled()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        var app = builder.Build();

        var result = app.MapAccountEndpoints();

        result.Should().BeSameAs(app);
        var routeBuilder = (Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app;
        var routePatterns = routeBuilder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText)
            .ToArray();
        routePatterns.Should().Contain("/account/login");
        routePatterns.Should().Contain("/account/logout");
    }
}
