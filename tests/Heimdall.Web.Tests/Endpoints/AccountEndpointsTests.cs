using System.Security.Claims;
using FluentAssertions;
using Heimdall.BLL.Email;
using Heimdall.Core.Auditing;
using Heimdall.Core.Email;
using Heimdall.Core.Models;
using Heimdall.Web.Email;
using Heimdall.Web.Endpoints;
using Heimdall.Web.Identity;
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
        routePatterns.Should().Contain("/account/forgot-password");
        routePatterns.Should().Contain("/account/reset-password");
        routePatterns.Should().Contain("/account/register");
        routePatterns.Should().Contain("/account/confirm-email");
    }

    // -----------------------------------------------------------------------------
    // Phase 1 step 10 helpers
    // -----------------------------------------------------------------------------

    private static EmailFlowGate ActiveGate() =>
        new(new EmailSenderRegistrationInfo
        {
            ChosenImplementation = "MailKitEmailSender",
            Reason = "test",
        });

    private static EmailFlowGate InactiveGate() =>
        new(new EmailSenderRegistrationInfo
        {
            ChosenImplementation = "NoOpEmailSender",
            Reason = "test",
        });

    private static IOptions<RegistrationOptions> RegistrationEnabled() =>
        Options.Create(new RegistrationOptions { Enabled = true });

    private static IOptions<RegistrationOptions> RegistrationDisabled() =>
        Options.Create(new RegistrationOptions { Enabled = false });

    // -----------------------------------------------------------------------------
    // Forgot-password
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task Should_RedirectToConfirmation_When_ForgotPasswordCalledWithKnownConfirmedEmail()
    {
        var ctx = CreateHttpContext();
        var user = SampleUser();
        user.EmailConfirmed = true;
        var um = CreateUserManagerMock();
        um.Setup(x => x.FindByEmailAsync(user.Email)).ReturnsAsync(user);
        um.Setup(x => x.GeneratePasswordResetTokenAsync(user)).ReturnsAsync("reset-token-xyz");
        var sender = new Mock<IEmailSender>();
        sender.Setup(x => x.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        var result = await AccountEndpoints.HandleForgotPasswordAsync(
            ctx, user.Email, um.Object, sender.Object, ActiveGate(), _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().Be("/forgot-password-confirmation");
        sender.Verify(x => x.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        um.Verify(x => x.GeneratePasswordResetTokenAsync(user), Times.Once);
        _audit.Verify(
            x => x.WriteAsync(It.Is<AuditEvent>(e => e.EventType == "password.reset.requested"
                                                     && e.ActorUserId == user.Id),
                              It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_RedirectToConfirmation_When_ForgotPasswordCalledWithUnknownEmail()
    {
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();
        um.Setup(x => x.FindByEmailAsync(It.IsAny<string>())).ReturnsAsync((HeimdallUser?)null);
        var sender = new Mock<IEmailSender>();

        var result = await AccountEndpoints.HandleForgotPasswordAsync(
            ctx, "ghost@example.com", um.Object, sender.Object, ActiveGate(), _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().Be("/forgot-password-confirmation");
        sender.Verify(x => x.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        _audit.Verify(
            x => x.WriteAsync(It.Is<AuditEvent>(e => e.EventType == "password.reset.requested.unknown_email"
                                                     && e.ActorUserId == null),
                              It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_RedirectToConfirmation_When_ForgotPasswordCalledWithUnconfirmedEmail()
    {
        var ctx = CreateHttpContext();
        var user = SampleUser();
        user.EmailConfirmed = false;
        var um = CreateUserManagerMock();
        um.Setup(x => x.FindByEmailAsync(user.Email)).ReturnsAsync(user);
        var sender = new Mock<IEmailSender>();

        var result = await AccountEndpoints.HandleForgotPasswordAsync(
            ctx, user.Email, um.Object, sender.Object, ActiveGate(), _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().Be("/forgot-password-confirmation");
        sender.Verify(x => x.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        _audit.Verify(
            x => x.WriteAsync(It.Is<AuditEvent>(e => e.EventType == "password.reset.requested.unknown_email"),
                              It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_RedirectToConfirmation_When_EmailSendThrows()
    {
        var ctx = CreateHttpContext();
        var user = SampleUser();
        user.EmailConfirmed = true;
        var um = CreateUserManagerMock();
        um.Setup(x => x.FindByEmailAsync(user.Email)).ReturnsAsync(user);
        um.Setup(x => x.GeneratePasswordResetTokenAsync(user)).ReturnsAsync("token");
        var sender = new Mock<IEmailSender>();
        sender.Setup(x => x.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("smtp down"));

        var result = await AccountEndpoints.HandleForgotPasswordAsync(
            ctx, user.Email, um.Object, sender.Object, ActiveGate(), _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().Be("/forgot-password-confirmation");
        _audit.Verify(
            x => x.WriteAsync(It.Is<AuditEvent>(e => e.EventType == "password.reset.send_failed"
                                                     && e.ActorUserId == user.Id),
                              It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_RedirectToForgotPasswordWithDisabled_When_GateInactive()
    {
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();
        var sender = new Mock<IEmailSender>();

        var result = await AccountEndpoints.HandleForgotPasswordAsync(
            ctx, "x@example.com", um.Object, sender.Object, InactiveGate(), _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().Be("/forgot-password?error=disabled");
        um.Verify(x => x.FindByEmailAsync(It.IsAny<string>()), Times.Never);
        sender.Verify(x => x.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -----------------------------------------------------------------------------
    // Reset-password
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task Should_RedirectToLoginWithReset_When_ResetSucceeds()
    {
        var ctx = CreateHttpContext();
        var user = SampleUser();
        var um = CreateUserManagerMock();
        um.Setup(x => x.FindByEmailAsync(user.Email)).ReturnsAsync(user);
        um.Setup(x => x.ResetPasswordAsync(user, "tok", "NewP@ssword12!"))
          .ReturnsAsync(IdentityResult.Success);

        var result = await AccountEndpoints.HandleResetPasswordAsync(
            ctx, user.Email, "tok", "NewP@ssword12!", "NewP@ssword12!",
            um.Object, ActiveGate(), _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().Be("/login?reset=success");
        _audit.Verify(
            x => x.WriteAsync(It.Is<AuditEvent>(e => e.EventType == "password.reset.success"
                                                     && e.ActorUserId == user.Id),
                              It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_RedirectToReset_When_PasswordsMismatch()
    {
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();

        var result = await AccountEndpoints.HandleResetPasswordAsync(
            ctx, "user@example.com", "tok", "P@ssword12!", "different",
            um.Object, ActiveGate(), _audit.Object, default);

        var url = result.Should().BeOfType<RedirectHttpResult>().Subject.Url;
        url.Should().Contain("/reset-password");
        url.Should().Contain("error=passwords-mismatch");
        um.Verify(x => x.FindByEmailAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Should_RedirectToResetWithError_When_TokenInvalid()
    {
        var ctx = CreateHttpContext();
        var user = SampleUser();
        var um = CreateUserManagerMock();
        um.Setup(x => x.FindByEmailAsync(user.Email)).ReturnsAsync(user);
        um.Setup(x => x.ResetPasswordAsync(user, It.IsAny<string>(), It.IsAny<string>()))
          .ReturnsAsync(IdentityResult.Failed(new IdentityError { Code = "InvalidToken", Description = "bad" }));

        var result = await AccountEndpoints.HandleResetPasswordAsync(
            ctx, user.Email, "bad-token", "NewP@ssword12!", "NewP@ssword12!",
            um.Object, ActiveGate(), _audit.Object, default);

        var url = result.Should().BeOfType<RedirectHttpResult>().Subject.Url;
        url.Should().Contain("error=invalid-token");
        _audit.Verify(
            x => x.WriteAsync(It.Is<AuditEvent>(e => e.EventType == "password.reset.failure.invalid_token"
                                                     && e.ActorUserId == user.Id),
                              It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_RedirectToResetWithError_When_UserNotFound()
    {
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();
        um.Setup(x => x.FindByEmailAsync(It.IsAny<string>())).ReturnsAsync((HeimdallUser?)null);

        var result = await AccountEndpoints.HandleResetPasswordAsync(
            ctx, "ghost@example.com", "tok", "NewP@ssword12!", "NewP@ssword12!",
            um.Object, ActiveGate(), _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().Be("/reset-password?error=invalid-token");
        _audit.Verify(
            x => x.WriteAsync(It.Is<AuditEvent>(e => e.EventType == "password.reset.failure.unknown_email"),
                              It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_Return404_When_GateInactive_OnResetPassword()
    {
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();

        var result = await AccountEndpoints.HandleResetPasswordAsync(
            ctx, "x@example.com", "t", "p", "p",
            um.Object, InactiveGate(), _audit.Object, default);

        result.Should().BeOfType<NotFound>();
        um.Verify(x => x.FindByEmailAsync(It.IsAny<string>()), Times.Never);
    }

    // -----------------------------------------------------------------------------
    // Register
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task Should_Return404_When_RegistrationDisabled()
    {
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();
        var sender = new Mock<IEmailSender>();

        var result = await AccountEndpoints.HandleRegisterAsync(
            ctx, "u@example.com", "P@ssword12!", "P@ssword12!",
            um.Object, sender.Object, ActiveGate(), RegistrationDisabled(), _audit.Object, default);

        result.Should().BeOfType<NotFound>();
        um.Verify(x => x.CreateAsync(It.IsAny<HeimdallUser>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Should_Return404_When_GateInactive_OnRegister()
    {
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();
        var sender = new Mock<IEmailSender>();

        var result = await AccountEndpoints.HandleRegisterAsync(
            ctx, "u@example.com", "P@ssword12!", "P@ssword12!",
            um.Object, sender.Object, InactiveGate(), RegistrationEnabled(), _audit.Object, default);

        result.Should().BeOfType<NotFound>();
    }

    [Fact]
    public async Task Should_RedirectToRegisterConfirmation_When_RegistrationSucceeds()
    {
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();
        um.Setup(x => x.NormalizeEmail(It.IsAny<string>())).Returns<string>(s => s.ToUpperInvariant());
        um.Setup(x => x.CreateAsync(It.IsAny<HeimdallUser>(), "P@ssword12!"))
          .ReturnsAsync(IdentityResult.Success);
        um.Setup(x => x.GenerateEmailConfirmationTokenAsync(It.IsAny<HeimdallUser>()))
          .ReturnsAsync("confirm-token");
        var sender = new Mock<IEmailSender>();
        sender.Setup(x => x.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        var result = await AccountEndpoints.HandleRegisterAsync(
            ctx, "new@example.com", "P@ssword12!", "P@ssword12!",
            um.Object, sender.Object, ActiveGate(), RegistrationEnabled(), _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().Be("/register-confirmation");
        sender.Verify(x => x.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        _audit.Verify(
            x => x.WriteAsync(It.Is<AuditEvent>(e => e.EventType == "account.register.success"
                                                     && e.PayloadJson.Contains("example.com")),
                              It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_RedirectToRegisterWithError_When_DuplicateUser()
    {
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();
        um.Setup(x => x.NormalizeEmail(It.IsAny<string>())).Returns<string>(s => s.ToUpperInvariant());
        um.Setup(x => x.CreateAsync(It.IsAny<HeimdallUser>(), It.IsAny<string>()))
          .ReturnsAsync(IdentityResult.Failed(new IdentityError { Code = "DuplicateUserName", Description = "exists" }));
        var sender = new Mock<IEmailSender>();

        var result = await AccountEndpoints.HandleRegisterAsync(
            ctx, "dup@example.com", "P@ssword12!", "P@ssword12!",
            um.Object, sender.Object, ActiveGate(), RegistrationEnabled(), _audit.Object, default);

        var url = result.Should().BeOfType<RedirectHttpResult>().Subject.Url;
        url.Should().Contain("/register?error=DuplicateUserName");
        _audit.Verify(
            x => x.WriteAsync(It.Is<AuditEvent>(e => e.EventType == "account.register.failure"
                                                     && e.PayloadJson.Contains("DuplicateUserName")),
                              It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_RedirectToRegisterWithError_When_RegisterPasswordsMismatch()
    {
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();
        var sender = new Mock<IEmailSender>();

        var result = await AccountEndpoints.HandleRegisterAsync(
            ctx, "u@example.com", "P@ssword12!", "different",
            um.Object, sender.Object, ActiveGate(), RegistrationEnabled(), _audit.Object, default);

        var url = result.Should().BeOfType<RedirectHttpResult>().Subject.Url;
        url.Should().Contain("/register?error=PasswordMismatch");
        um.Verify(x => x.CreateAsync(It.IsAny<HeimdallUser>(), It.IsAny<string>()), Times.Never);
    }

    // -----------------------------------------------------------------------------
    // Confirm-email
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task Should_RedirectToLoginWithSuccess_When_ConfirmEmailValid()
    {
        var ctx = CreateHttpContext();
        var user = SampleUser();
        var um = CreateUserManagerMock();
        um.Setup(x => x.FindByEmailAsync(user.Email)).ReturnsAsync(user);
        um.Setup(x => x.ConfirmEmailAsync(user, "tok")).ReturnsAsync(IdentityResult.Success);

        var result = await AccountEndpoints.HandleConfirmEmailAsync(
            ctx, user.Email, "tok", um.Object, _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().Be("/login?confirm=success");
        _audit.Verify(
            x => x.WriteAsync(It.Is<AuditEvent>(e => e.EventType == "account.confirm_email.success"
                                                     && e.ActorUserId == user.Id),
                              It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_RedirectToLoginWithInvalid_When_ConfirmEmailUnknownUser()
    {
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();
        um.Setup(x => x.FindByEmailAsync(It.IsAny<string>())).ReturnsAsync((HeimdallUser?)null);

        var result = await AccountEndpoints.HandleConfirmEmailAsync(
            ctx, "ghost@example.com", "tok", um.Object, _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().Be("/login?confirm=invalid");
        _audit.Verify(
            x => x.WriteAsync(It.Is<AuditEvent>(e => e.EventType == "account.confirm_email.failure.unknown_email"),
                              It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_RedirectToLoginWithInvalid_When_ConfirmEmailTokenInvalid()
    {
        var ctx = CreateHttpContext();
        var user = SampleUser();
        var um = CreateUserManagerMock();
        um.Setup(x => x.FindByEmailAsync(user.Email)).ReturnsAsync(user);
        um.Setup(x => x.ConfirmEmailAsync(user, It.IsAny<string>()))
          .ReturnsAsync(IdentityResult.Failed(new IdentityError { Code = "InvalidToken" }));

        var result = await AccountEndpoints.HandleConfirmEmailAsync(
            ctx, user.Email, "bad", um.Object, _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().Be("/login?confirm=invalid");
        _audit.Verify(
            x => x.WriteAsync(It.Is<AuditEvent>(e => e.EventType == "account.confirm_email.failure.invalid_token"
                                                     && e.ActorUserId == user.Id),
                              It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
