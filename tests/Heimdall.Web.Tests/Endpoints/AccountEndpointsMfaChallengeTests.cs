using System.Security.Claims;
using FluentAssertions;
using Heimdall.Core.Auditing;
using Heimdall.Core.Models;
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
using IdentitySignInResult = Microsoft.AspNetCore.Identity.SignInResult;

namespace Heimdall.Web.Tests.Endpoints;

/// <summary>
/// Unit tests for the Phase 4.5 step 12/13/14 MFA challenge / recovery /
/// regenerate handlers in <see cref="AccountEndpoints"/>. Mirrors the doubles
/// pattern from <see cref="AccountEndpointsMfaTests"/>.
/// </summary>
public class AccountEndpointsMfaChallengeTests
{
    private readonly Mock<IAuditEventWriter> _audit = new();
    private readonly Mock<IRecoveryCodeDisplayCache> _recoveryCache = new();

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

    private static ClaimsPrincipal AuthenticatedPrincipal(HeimdallUser user)
        => new(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()) },
            authenticationType: "TestAuth"));

    // -----------------------------------------------------------------------------
    // HandleMfaChallengeAsync
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task Should_RedirectToLoginExpired_When_TwoFactorUserMissing()
    {
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.GetTwoFactorAuthenticationUserAsync()).ReturnsAsync((HeimdallUser?)null);

        var result = await AccountEndpoints.HandleMfaChallengeAsync(
            ctx, "123456", rememberMachine: false, rememberMe: false, returnUrl: "/",
            sm.Object, _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>()
              .Subject.Url.Should().Be("/login?error=mfa-expired");
    }

    [Fact]
    public async Task Should_RedirectToReturnUrlAndAudit_When_ChallengeSucceeds()
    {
        var user = SampleUser();
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.GetTwoFactorAuthenticationUserAsync()).ReturnsAsync(user);
        sm.Setup(x => x.TwoFactorAuthenticatorSignInAsync("123456", false, false))
          .ReturnsAsync(IdentitySignInResult.Success);

        var captured = new List<AuditEvent>();
        _audit.Setup(x => x.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
              .Callback<AuditEvent, CancellationToken>((e, _) => captured.Add(e))
              .Returns(Task.CompletedTask);

        var result = await AccountEndpoints.HandleMfaChallengeAsync(
            ctx, "123 456", rememberMachine: false, rememberMe: false, returnUrl: "/admin/audit",
            sm.Object, _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>()
              .Subject.Url.Should().Be("/admin/audit");
        captured.Should().ContainSingle(e => e.EventType == "mfa.challenge.succeeded");
    }

    [Fact]
    public async Task Should_StripCodeSeparatorsBeforeCallingIdentity()
    {
        var user = SampleUser();
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.GetTwoFactorAuthenticationUserAsync()).ReturnsAsync(user);
        sm.Setup(x => x.TwoFactorAuthenticatorSignInAsync("123456", true, true))
          .ReturnsAsync(IdentitySignInResult.Success);

        var result = await AccountEndpoints.HandleMfaChallengeAsync(
            ctx, "123-456", rememberMachine: true, rememberMe: true, returnUrl: null,
            sm.Object, _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>();
        sm.Verify(x => x.TwoFactorAuthenticatorSignInAsync("123456", true, true), Times.Once);
    }

    [Fact]
    public async Task Should_RedirectToLockedOut_When_ChallengeIsLockedOut()
    {
        var user = SampleUser();
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.GetTwoFactorAuthenticationUserAsync()).ReturnsAsync(user);
        sm.Setup(x => x.TwoFactorAuthenticatorSignInAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
          .ReturnsAsync(IdentitySignInResult.LockedOut);

        var captured = new List<AuditEvent>();
        _audit.Setup(x => x.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
              .Callback<AuditEvent, CancellationToken>((e, _) => captured.Add(e))
              .Returns(Task.CompletedTask);

        var result = await AccountEndpoints.HandleMfaChallengeAsync(
            ctx, "000000", rememberMachine: false, rememberMe: false, returnUrl: "/",
            sm.Object, _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>()
              .Subject.Url.Should().Be("/login?error=locked-out");
        captured.Should().ContainSingle(e => e.EventType == "mfa.challenge.failed"
            && e.PayloadJson != null && e.PayloadJson.Contains("locked_out"));
    }

    [Fact]
    public async Task Should_RedirectBackToChallengeWithInvalidCode_When_ChallengeFails()
    {
        var user = SampleUser();
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.GetTwoFactorAuthenticationUserAsync()).ReturnsAsync(user);
        sm.Setup(x => x.TwoFactorAuthenticatorSignInAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
          .ReturnsAsync(IdentitySignInResult.Failed);

        var result = await AccountEndpoints.HandleMfaChallengeAsync(
            ctx, "000000", rememberMachine: false, rememberMe: true, returnUrl: "/x",
            sm.Object, _audit.Object, default);

        var redirect = result.Should().BeOfType<RedirectHttpResult>().Subject;
        redirect.Url.Should().StartWith("/account/mfa/challenge?returnUrl=");
        redirect.Url.Should().Contain("error=invalid-code");
        redirect.Url.Should().Contain("rememberMe=true");
    }

    // -----------------------------------------------------------------------------
    // HandleMfaRecoveryAsync
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task Should_RedirectToLoginExpired_When_RecoveryWithoutTwoFactorUser()
    {
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.GetTwoFactorAuthenticationUserAsync()).ReturnsAsync((HeimdallUser?)null);

        var result = await AccountEndpoints.HandleMfaRecoveryAsync(
            ctx, "AAAA-BBBB", returnUrl: "/", sm.Object, um.Object, _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>()
              .Subject.Url.Should().Be("/login?error=mfa-expired");
    }

    [Fact]
    public async Task Should_AppendLowCodesWarning_When_RecoveryLeavesFewerThanThreeCodes()
    {
        var user = SampleUser();
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.GetTwoFactorAuthenticationUserAsync()).ReturnsAsync(user);
        sm.Setup(x => x.TwoFactorRecoveryCodeSignInAsync("RECOVERYCODE"))
          .ReturnsAsync(IdentitySignInResult.Success);
        um.Setup(x => x.CountRecoveryCodesAsync(user)).ReturnsAsync(2);

        var captured = new List<AuditEvent>();
        _audit.Setup(x => x.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
              .Callback<AuditEvent, CancellationToken>((e, _) => captured.Add(e))
              .Returns(Task.CompletedTask);

        var result = await AccountEndpoints.HandleMfaRecoveryAsync(
            ctx, "RECOVERY-CODE", returnUrl: "/admin/audit",
            sm.Object, um.Object, _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>()
              .Subject.Url.Should().Be("/admin/audit?mfaRecoveryWarning=low");
        captured.Should().ContainSingle(e => e.EventType == "mfa.recovery.redeemed");
    }

    [Fact]
    public async Task Should_AppendLowCodesWarningWithAmpersand_When_ReturnUrlHasQueryString()
    {
        var user = SampleUser();
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.GetTwoFactorAuthenticationUserAsync()).ReturnsAsync(user);
        sm.Setup(x => x.TwoFactorRecoveryCodeSignInAsync(It.IsAny<string>()))
          .ReturnsAsync(IdentitySignInResult.Success);
        um.Setup(x => x.CountRecoveryCodesAsync(user)).ReturnsAsync(1);

        var result = await AccountEndpoints.HandleMfaRecoveryAsync(
            ctx, "CODE", returnUrl: "/x?y=z", sm.Object, um.Object, _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>()
              .Subject.Url.Should().Be("/x?y=z&mfaRecoveryWarning=low");
    }

    [Fact]
    public async Task Should_NotAppendWarning_When_RemainingCodesAtOrAboveThreshold()
    {
        var user = SampleUser();
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.GetTwoFactorAuthenticationUserAsync()).ReturnsAsync(user);
        sm.Setup(x => x.TwoFactorRecoveryCodeSignInAsync(It.IsAny<string>()))
          .ReturnsAsync(IdentitySignInResult.Success);
        um.Setup(x => x.CountRecoveryCodesAsync(user)).ReturnsAsync(5);

        var result = await AccountEndpoints.HandleMfaRecoveryAsync(
            ctx, "CODE", returnUrl: "/home", sm.Object, um.Object, _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>()
              .Subject.Url.Should().Be("/home");
    }

    [Fact]
    public async Task Should_RedirectBackToRecoveryWithError_When_RecoveryCodeInvalid()
    {
        var user = SampleUser();
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.GetTwoFactorAuthenticationUserAsync()).ReturnsAsync(user);
        sm.Setup(x => x.TwoFactorRecoveryCodeSignInAsync(It.IsAny<string>()))
          .ReturnsAsync(IdentitySignInResult.Failed);

        var captured = new List<AuditEvent>();
        _audit.Setup(x => x.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
              .Callback<AuditEvent, CancellationToken>((e, _) => captured.Add(e))
              .Returns(Task.CompletedTask);

        var result = await AccountEndpoints.HandleMfaRecoveryAsync(
            ctx, "bad-code", returnUrl: "/", sm.Object, um.Object, _audit.Object, default);

        var redirect = result.Should().BeOfType<RedirectHttpResult>().Subject;
        redirect.Url.Should().StartWith("/account/mfa/recovery?returnUrl=");
        redirect.Url.Should().Contain("error=invalid-code");
        captured.Should().ContainSingle(e => e.EventType == "mfa.recovery.failed");
    }

    // -----------------------------------------------------------------------------
    // HandleMfaRecoveryCodesRegenerateAsync
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task Should_RedirectToLogin_When_RegenerateCalledAnonymously()
    {
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();
        um.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync((HeimdallUser?)null);
        var sm = CreateSignInManagerMock(um.Object);

        var result = await AccountEndpoints.HandleMfaRecoveryCodesRegenerateAsync(
            ctx, "password", um.Object, sm.Object, _recoveryCache.Object, _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>().Subject.Url.Should().Be("/login");
    }

    [Fact]
    public async Task Should_RedirectWithInvalidPasswordError_When_RegenerateBadPassword()
    {
        var user = SampleUser();
        var ctx = CreateHttpContext(AuthenticatedPrincipal(user));
        var um = CreateUserManagerMock();
        um.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(user);
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.CheckPasswordSignInAsync(user, "wrong", false))
          .ReturnsAsync(IdentitySignInResult.Failed);

        var captured = new List<AuditEvent>();
        _audit.Setup(x => x.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
              .Callback<AuditEvent, CancellationToken>((e, _) => captured.Add(e))
              .Returns(Task.CompletedTask);

        var result = await AccountEndpoints.HandleMfaRecoveryCodesRegenerateAsync(
            ctx, "wrong", um.Object, sm.Object, _recoveryCache.Object, _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>()
              .Subject.Url.Should().Be("/account/mfa/recovery-codes/regenerate?error=invalid-password");
        captured.Should().ContainSingle(e => e.EventType == "mfa.recovery_codes.regenerate.bad_password");
        um.Verify(x => x.GenerateNewTwoFactorRecoveryCodesAsync(It.IsAny<HeimdallUser>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Should_StashCodesAndRedirectToDisplay_When_RegenerateSucceeds()
    {
        var user = SampleUser();
        var ctx = CreateHttpContext(AuthenticatedPrincipal(user));
        var um = CreateUserManagerMock();
        um.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(user);
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.CheckPasswordSignInAsync(user, "good", false))
          .ReturnsAsync(IdentitySignInResult.Success);

        var codes = Enumerable.Range(1, 10).Select(i => $"CODE-{i:00}").ToArray();
        um.Setup(x => x.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))
          .ReturnsAsync(codes);

        var token = Guid.NewGuid();
        _recoveryCache.Setup(x => x.Stash(user.Id, It.Is<IReadOnlyList<string>>(c => c.Count == 10)))
                      .Returns(token);

        var captured = new List<AuditEvent>();
        _audit.Setup(x => x.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
              .Callback<AuditEvent, CancellationToken>((e, _) => captured.Add(e))
              .Returns(Task.CompletedTask);

        var result = await AccountEndpoints.HandleMfaRecoveryCodesRegenerateAsync(
            ctx, "good", um.Object, sm.Object, _recoveryCache.Object, _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>()
              .Subject.Url.Should().Be(
                  $"/account/mfa/recovery-codes?token={Uri.EscapeDataString(token.ToString())}&from=regenerate");
        captured.Should().ContainSingle(e => e.EventType == "mfa.recovery_codes.regenerated");
        var regenerated = captured.Single(e => e.EventType == "mfa.recovery_codes.regenerated");
        regenerated.PayloadJson.Should().Contain("\"recovery_code_count\":10");
        foreach (string code in codes)
        {
            regenerated.PayloadJson.Should().NotContain(code, "raw recovery codes must not be audited");
        }
    }
}
