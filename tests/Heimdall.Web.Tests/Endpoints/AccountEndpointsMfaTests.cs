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

namespace Heimdall.Web.Tests.Endpoints;

/// <summary>
/// Unit tests for the Phase 4.3 / 4.4 MFA setup-verify and disable handlers in
/// <see cref="AccountEndpoints"/>. Drives the static handlers directly with a
/// <see cref="DefaultHttpContext"/> and Moq doubles — same pattern as the
/// existing <see cref="AccountEndpointsTests"/>.
/// </summary>
public class AccountEndpointsMfaTests
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
    // HandleMfaSetupVerifyAsync
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task Should_RedirectToLogin_When_MfaSetupVerifyCalledAnonymously()
    {
        // Arrange
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();
        um.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync((HeimdallUser?)null);

        // Act
        var result = await AccountEndpoints.HandleMfaSetupVerifyAsync(
            ctx, "123456", um.Object, _recoveryCache.Object, _audit.Object, default);

        // Assert
        var redirect = result.Should().BeOfType<RedirectHttpResult>().Subject;
        redirect.Url.Should().Be("/login");

        _audit.Verify(
            x => x.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "anonymous callers must not produce audit noise");
        um.Verify(
            x => x.SetTwoFactorEnabledAsync(It.IsAny<HeimdallUser>(), It.IsAny<bool>()),
            Times.Never);
        _recoveryCache.Verify(
            x => x.Stash(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>>()),
            Times.Never);
    }

    [Fact]
    public async Task Should_RedirectToSetupWithError_When_MfaSetupVerifyCodeIsInvalid()
    {
        // Arrange
        var user = SampleUser();
        var ctx = CreateHttpContext(AuthenticatedPrincipal(user));
        var um = CreateUserManagerMock();
        um.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(user);
        um.Setup(x => x.VerifyTwoFactorTokenAsync(
                user, TokenOptions.DefaultAuthenticatorProvider, "wrong-code"))
          .ReturnsAsync(false);

        var captured = new List<AuditEvent>();
        _audit.Setup(x => x.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
              .Callback<AuditEvent, CancellationToken>((e, _) => captured.Add(e))
              .Returns(Task.CompletedTask);

        // Act
        var result = await AccountEndpoints.HandleMfaSetupVerifyAsync(
            ctx, "wrong-code", um.Object, _recoveryCache.Object, _audit.Object, default);

        // Assert
        var redirect = result.Should().BeOfType<RedirectHttpResult>().Subject;
        redirect.Url.Should().Be("/account/mfa/setup?error=invalid-code");

        captured.Should().ContainSingle(e => e.EventType == "mfa.enrolment.verify_failed");
        var failureEvent = captured.Single(e => e.EventType == "mfa.enrolment.verify_failed");
        failureEvent.ActorUserId.Should().Be(user.Id);
        failureEvent.Target.Should().Be(user.Id.ToString());
        failureEvent.PayloadJson.Should().NotContain("wrong-code",
            "the submitted TOTP code must never appear in audit payloads");

        um.Verify(
            x => x.SetTwoFactorEnabledAsync(It.IsAny<HeimdallUser>(), true),
            Times.Never,
            "an invalid code must not flip TwoFactorEnabled");
        um.Verify(
            x => x.GenerateNewTwoFactorRecoveryCodesAsync(It.IsAny<HeimdallUser>(), It.IsAny<int>()),
            Times.Never);
        _recoveryCache.Verify(
            x => x.Stash(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>>()),
            Times.Never);
    }

    [Fact]
    public async Task Should_EnableMfaAndStashRecoveryCodes_When_MfaSetupVerifyCodeIsValid()
    {
        // Arrange
        var user = SampleUser();
        var ctx = CreateHttpContext(AuthenticatedPrincipal(user));
        var um = CreateUserManagerMock();
        um.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(user);
        um.Setup(x => x.VerifyTwoFactorTokenAsync(
                user, TokenOptions.DefaultAuthenticatorProvider, "123456"))
          .ReturnsAsync(true);
        um.Setup(x => x.SetTwoFactorEnabledAsync(user, true))
          .ReturnsAsync(IdentityResult.Success);

        var generatedCodes = new[]
        {
            "RECOVERY-AAA-001",
            "RECOVERY-AAA-002",
            "RECOVERY-AAA-003",
            "RECOVERY-AAA-004",
            "RECOVERY-AAA-005",
            "RECOVERY-AAA-006",
            "RECOVERY-AAA-007",
            "RECOVERY-AAA-008",
            "RECOVERY-AAA-009",
            "RECOVERY-AAA-010",
        };
        um.Setup(x => x.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))
          .ReturnsAsync(generatedCodes);

        var displayToken = Guid.NewGuid();
        _recoveryCache.Setup(x => x.Stash(user.Id, It.IsAny<IReadOnlyList<string>>()))
                      .Returns(displayToken);

        var captured = new List<AuditEvent>();
        _audit.Setup(x => x.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
              .Callback<AuditEvent, CancellationToken>((e, _) => captured.Add(e))
              .Returns(Task.CompletedTask);

        // Act
        var result = await AccountEndpoints.HandleMfaSetupVerifyAsync(
            ctx, "123456", um.Object, _recoveryCache.Object, _audit.Object, default);

        // Assert — redirect to the display page with the opaque token.
        var redirect = result.Should().BeOfType<RedirectHttpResult>().Subject;
        redirect.Url.Should().Be($"/account/mfa/recovery-codes?token={Uri.EscapeDataString(displayToken.ToString())}");

        um.Verify(x => x.SetTwoFactorEnabledAsync(user, true), Times.Once);
        um.Verify(x => x.GenerateNewTwoFactorRecoveryCodesAsync(user, 10), Times.Once);
        _recoveryCache.Verify(
            x => x.Stash(user.Id, It.Is<IReadOnlyList<string>>(c => c.Count == 10)),
            Times.Once);

        // Audit event written exactly once with the right shape.
        captured.Should().ContainSingle(e => e.EventType == "mfa_enrolled");
        var enrolled = captured.Single(e => e.EventType == "mfa_enrolled");
        enrolled.ActorUserId.Should().Be(user.Id);
        enrolled.Target.Should().Be(user.Id.ToString());
        enrolled.PayloadJson.Should().Contain("\"recovery_code_count\":10");
        enrolled.PayloadJson.Should().NotContain("123456",
            "the submitted TOTP code must never appear in audit payloads");
        foreach (var code in generatedCodes)
        {
            enrolled.PayloadJson.Should().NotContain(code,
                "raw recovery codes must never appear in audit payloads");
        }
    }

    [Fact]
    public async Task Should_TreatNullGeneratedCodesAsEmpty_When_RecoveryStoreReturnsNull()
    {
        // Defence-in-depth path: generator returns null; the handler must still
        // complete and stash an empty list (audit count = 0).
        var user = SampleUser();
        var ctx = CreateHttpContext(AuthenticatedPrincipal(user));
        var um = CreateUserManagerMock();
        um.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(user);
        um.Setup(x => x.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, "000000"))
          .ReturnsAsync(true);
        um.Setup(x => x.SetTwoFactorEnabledAsync(user, true)).ReturnsAsync(IdentityResult.Success);
        um.Setup(x => x.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))
          .ReturnsAsync((IEnumerable<string>?)null);
        _recoveryCache.Setup(x => x.Stash(user.Id, It.IsAny<IReadOnlyList<string>>()))
                      .Returns(Guid.NewGuid());

        var captured = new List<AuditEvent>();
        _audit.Setup(x => x.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
              .Callback<AuditEvent, CancellationToken>((e, _) => captured.Add(e))
              .Returns(Task.CompletedTask);

        var result = await AccountEndpoints.HandleMfaSetupVerifyAsync(
            ctx, "000000", um.Object, _recoveryCache.Object, _audit.Object, default);

        result.Should().BeOfType<RedirectHttpResult>();
        _recoveryCache.Verify(
            x => x.Stash(user.Id, It.Is<IReadOnlyList<string>>(c => c.Count == 0)),
            Times.Once);
        captured.Single(e => e.EventType == "mfa_enrolled").PayloadJson
            .Should().Contain("\"recovery_code_count\":0");
    }

    // -----------------------------------------------------------------------------
    // HandleMfaDisableAsync
    // -----------------------------------------------------------------------------

    /// <summary>
    /// Test double for the dual-typed user store registered by
    /// AddHeimdallIdentityStores: implements <see cref="IUserStore{TUser}"/>
    /// AND <see cref="IUserTwoFactorRecoveryCodeStore{TUser}"/> so the handler's
    /// runtime cast succeeds.
    /// </summary>
    public interface IDualUserStore
        : IUserStore<HeimdallUser>, IUserTwoFactorRecoveryCodeStore<HeimdallUser>
    {
    }

    [Fact]
    public async Task Should_RedirectToLogin_When_MfaDisableCalledAnonymously()
    {
        var ctx = CreateHttpContext();
        var um = CreateUserManagerMock();
        um.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync((HeimdallUser?)null);
        var sm = CreateSignInManagerMock(um.Object);
        var store = new Mock<IDualUserStore>();

        var result = await AccountEndpoints.HandleMfaDisableAsync(
            ctx, "pw", um.Object, sm.Object, store.Object, _audit.Object, default);

        var redirect = result.Should().BeOfType<RedirectHttpResult>().Subject;
        redirect.Url.Should().Be("/login");

        _audit.Verify(
            x => x.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
        um.Verify(
            x => x.SetTwoFactorEnabledAsync(It.IsAny<HeimdallUser>(), It.IsAny<bool>()),
            Times.Never);
        sm.Verify(x => x.SignOutAsync(), Times.Never);
    }

    [Fact]
    public async Task Should_RedirectWithError_When_MfaDisablePasswordIsInvalid()
    {
        var user = SampleUser();
        var ctx = CreateHttpContext(AuthenticatedPrincipal(user));
        var um = CreateUserManagerMock();
        um.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(user);
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.CheckPasswordSignInAsync(user, "bad", false))
          .ReturnsAsync(SignInResult.Failed);
        var store = new Mock<IDualUserStore>();

        var captured = new List<AuditEvent>();
        _audit.Setup(x => x.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
              .Callback<AuditEvent, CancellationToken>((e, _) => captured.Add(e))
              .Returns(Task.CompletedTask);

        var result = await AccountEndpoints.HandleMfaDisableAsync(
            ctx, "bad", um.Object, sm.Object, store.Object, _audit.Object, default);

        var redirect = result.Should().BeOfType<RedirectHttpResult>().Subject;
        redirect.Url.Should().Be("/account/mfa/disable?error=invalid-password");

        captured.Should().ContainSingle(e => e.EventType == "mfa.disable.bad_password");
        var bad = captured.Single();
        bad.ActorUserId.Should().Be(user.Id);
        bad.PayloadJson.Should().NotContain("bad", "the submitted password must never appear in audit payloads");

        um.Verify(
            x => x.SetTwoFactorEnabledAsync(It.IsAny<HeimdallUser>(), false),
            Times.Never,
            "a wrong password must not disable MFA");
        um.Verify(x => x.ResetAuthenticatorKeyAsync(It.IsAny<HeimdallUser>()), Times.Never);
        store.Verify(
            x => x.ReplaceCodesAsync(It.IsAny<HeimdallUser>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        um.Verify(x => x.UpdateSecurityStampAsync(It.IsAny<HeimdallUser>()), Times.Never);
        sm.Verify(x => x.SignOutAsync(), Times.Never);
    }

    [Fact]
    public async Task Should_TearDownInOrderAndSignOut_When_MfaDisablePasswordIsValid()
    {
        // Arrange
        var user = SampleUser();
        var ctx = CreateHttpContext(AuthenticatedPrincipal(user));
        var um = CreateUserManagerMock();
        um.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(user);
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.CheckPasswordSignInAsync(user, "correct-pw", false))
          .ReturnsAsync(SignInResult.Success);

        // Track invocation order via a shared list keyed on method name.
        var callOrder = new List<string>();
        um.Setup(x => x.ResetAuthenticatorKeyAsync(user))
          .Callback(() => callOrder.Add("ResetAuthenticatorKeyAsync"))
          .ReturnsAsync(IdentityResult.Success);
        um.Setup(x => x.SetTwoFactorEnabledAsync(user, false))
          .Callback(() => callOrder.Add("SetTwoFactorEnabledAsync"))
          .ReturnsAsync(IdentityResult.Success);
        um.Setup(x => x.UpdateSecurityStampAsync(user))
          .Callback(() => callOrder.Add("UpdateSecurityStampAsync"))
          .ReturnsAsync(IdentityResult.Success);

        var store = new Mock<IDualUserStore>();
        store.Setup(x => x.ReplaceCodesAsync(user, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
             .Callback<HeimdallUser, IEnumerable<string>, CancellationToken>(
                 (_, codes, _) =>
                 {
                     callOrder.Add("ReplaceCodesAsync");
                     codes.Should().BeEmpty("disable wipes every recovery code");
                 })
             .Returns(Task.CompletedTask);

        sm.Setup(x => x.SignOutAsync())
          .Callback(() => callOrder.Add("SignOutAsync"))
          .Returns(Task.CompletedTask);

        var captured = new List<AuditEvent>();
        _audit.Setup(x => x.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
              .Callback<AuditEvent, CancellationToken>((e, _) => captured.Add(e))
              .Returns(Task.CompletedTask);

        // Act
        var result = await AccountEndpoints.HandleMfaDisableAsync(
            ctx, "correct-pw", um.Object, sm.Object, store.Object, _audit.Object, default);

        // Assert — redirect target.
        var redirect = result.Should().BeOfType<RedirectHttpResult>().Subject;
        redirect.Url.Should().Be("/login?info=mfa-disabled");

        // The tear-down order documented in AccountEndpoints.cs:
        //   ResetAuthenticatorKey → SetTwoFactorEnabled(false) → ReplaceCodes(empty)
        //   → UpdateSecurityStamp → SignOut
        callOrder.Should().ContainInOrder(
            "ResetAuthenticatorKeyAsync",
            "SetTwoFactorEnabledAsync",
            "ReplaceCodesAsync",
            "UpdateSecurityStampAsync",
            "SignOutAsync");

        // Each step exactly once.
        um.Verify(x => x.ResetAuthenticatorKeyAsync(user), Times.Once);
        um.Verify(x => x.SetTwoFactorEnabledAsync(user, false), Times.Once);
        store.Verify(
            x => x.ReplaceCodesAsync(user, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Once);
        um.Verify(x => x.UpdateSecurityStampAsync(user), Times.Once);
        sm.Verify(x => x.SignOutAsync(), Times.Once);

        // Audit — exactly one mfa_disabled event with the minimal payload.
        captured.Should().ContainSingle(e => e.EventType == "mfa_disabled");
        var disabled = captured.Single();
        disabled.ActorUserId.Should().Be(user.Id);
        disabled.Target.Should().Be(user.Id.ToString());
        disabled.PayloadJson.Should().Contain("\"user_id\"");
        disabled.PayloadJson.Should().NotContain("correct-pw",
            "the submitted password must never appear in audit payloads");
    }

    [Fact]
    public async Task Should_Throw_When_MfaDisableUserStoreDoesNotImplementRecoveryCodeStore()
    {
        // Mis-wired host: IUserStore<HeimdallUser> registered without the
        // recovery-code store facet — handler must throw rather than silently
        // leave recovery codes alive.
        var user = SampleUser();
        var ctx = CreateHttpContext(AuthenticatedPrincipal(user));
        var um = CreateUserManagerMock();
        um.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(user);
        var sm = CreateSignInManagerMock(um.Object);
        sm.Setup(x => x.CheckPasswordSignInAsync(user, "pw", false)).ReturnsAsync(SignInResult.Success);
        um.Setup(x => x.ResetAuthenticatorKeyAsync(user)).ReturnsAsync(IdentityResult.Success);
        um.Setup(x => x.SetTwoFactorEnabledAsync(user, false)).ReturnsAsync(IdentityResult.Success);

        // Plain IUserStore — does NOT implement IUserTwoFactorRecoveryCodeStore.
        var brokenStore = new Mock<IUserStore<HeimdallUser>>();

        Func<Task> act = () => AccountEndpoints.HandleMfaDisableAsync(
            ctx, "pw", um.Object, sm.Object, brokenStore.Object, _audit.Object, default);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
