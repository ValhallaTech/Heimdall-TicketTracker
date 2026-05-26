using System;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.BLL.Tokens;
using Heimdall.Core.Auditing;
using Heimdall.Core.Interfaces;
using Heimdall.Web.Authentication;
using Heimdall.Web.Bootstrap;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Heimdall.Web.Tests.Authentication;

/// <summary>
/// Phase 5.5 step 12 — unit tests for <see cref="JwtBearerDenylistEvents"/>.
/// Exercises the happy path, the security-review hardened outage handler
/// (FGA OR system_admin), the deny-closed exception posture, and the
/// non-Redis exception propagation contract.
/// </summary>
/// <remarks>
/// Pure unit tests: no Autofac container, no Testcontainers. <see cref="TokenValidatedContext"/>
/// is constructed against a hand-built <see cref="DefaultHttpContext"/> whose
/// <see cref="IServiceProvider"/> resolves the mocked dependencies.
/// </remarks>
public class JwtBearerDenylistEventsTests
{
    private static readonly Guid ActorId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SeedOrgId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string Jti = "test-jti-abc123";

    private readonly Mock<IAccessTokenDenylist> _denylist = new(MockBehavior.Strict);
    private readonly Mock<IOpenFgaAuthorizationService> _fga = new(MockBehavior.Strict);
    private readonly Mock<IUserLookup> _userLookup = new(MockBehavior.Strict);
    private readonly Mock<IAuditEventWriter> _auditWriter = new(MockBehavior.Strict);
    private readonly Mock<IOptionsMonitor<SeedOrganizationOptions>> _seedOptions = new();

    public JwtBearerDenylistEventsTests()
    {
        _seedOptions
            .SetupGet(m => m.CurrentValue)
            .Returns(new SeedOrganizationOptions { OrganizationId = SeedOrgId });
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private ServiceProvider BuildServices(
        bool registerFga = true,
        bool registerAuditWriter = true,
        bool registerSeedOptions = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(_denylist.Object);
        services.AddSingleton(_userLookup.Object);
        if (registerFga)
        {
            services.AddSingleton(_fga.Object);
        }

        if (registerAuditWriter)
        {
            services.AddSingleton(_auditWriter.Object);
        }

        if (registerSeedOptions)
        {
            services.AddSingleton(_seedOptions.Object);
        }

        return services.BuildServiceProvider();
    }

    private static TokenValidatedContext BuildContext(IServiceProvider sp, ClaimsPrincipal principal)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = sp,
        };
        http.Connection.RemoteIpAddress = IPAddress.Loopback;
        var scheme = new AuthenticationScheme(
            JwtBearerDefaults.AuthenticationScheme,
            displayName: null,
            handlerType: typeof(JwtBearerHandler));
        var ctx = new TokenValidatedContext(http, scheme, new JwtBearerOptions())
        {
            Principal = principal,
        };
        return ctx;
    }

    private static ClaimsPrincipal PrincipalWith(string? jti = Jti, Guid? sub = null)
    {
        var claims = new System.Collections.Generic.List<Claim>();
        if (jti is not null)
        {
            claims.Add(new Claim("jti", jti));
        }

        if (sub is not null)
        {
            claims.Add(new Claim("sub", sub.Value.ToString()));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private void SetupFgaAdmin(bool isOrgAdmin) =>
        _fga
            .Setup(s => s.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(isOrgAdmin);

    private void SetupFgaThrows(Exception ex) =>
        _fga
            .Setup(s => s.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ex);

    private void SetupSystemAdmin(bool isSystemAdmin) =>
        _userLookup
            .Setup(u => u.IsSystemAdminAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(isSystemAdmin);

    private void SetupSystemAdminThrows(Exception ex) =>
        _userLookup
            .Setup(u => u.IsSystemAdminAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ex);

    private void SetupAuditWriteOk() =>
        _auditWriter
            .Setup(w => w.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

    // -------------------------------------------------------------------------
    // 1. Happy path — miss
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Should_LeaveContextUntouched_When_DenylistMisses()
    {
        _denylist
            .Setup(d => d.IsDeniedAsync(Jti, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DenylistLookup(Denied: false, Reason: null));

        using ServiceProvider sp = BuildServices();
        TokenValidatedContext ctx = BuildContext(sp, PrincipalWith(sub: ActorId));

        await JwtBearerDenylistEvents.OnTokenValidatedAsync(ctx);

        ctx.Result.Should().BeNull(
            because: "a miss must not touch ctx — downstream handlers decide");
    }

    // -------------------------------------------------------------------------
    // 2. Happy path — hit
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Should_FailWithDenylisted_When_JtiIsOnDenylist()
    {
        _denylist
            .Setup(d => d.IsDeniedAsync(Jti, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DenylistLookup(Denied: true, Reason: "logout"));

        using ServiceProvider sp = BuildServices();
        TokenValidatedContext ctx = BuildContext(sp, PrincipalWith(sub: ActorId));

        await JwtBearerDenylistEvents.OnTokenValidatedAsync(ctx);

        ctx.Result.Should().NotBeNull();
        ctx.Result!.Failure.Should().NotBeNull();
        ctx.Result.Failure!.Message.Should().Be("denylisted");
    }

    // -------------------------------------------------------------------------
    // 3. Redis outage — non-admin actor → fail-open + audit
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Should_AdmitAndAudit_When_RedisOutsAndActorIsNotAdmin()
    {
        _denylist
            .Setup(d => d.IsDeniedAsync(Jti, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "boom"));

        SetupFgaAdmin(false);
        SetupSystemAdmin(false);
        SetupAuditWriteOk();

        using ServiceProvider sp = BuildServices();
        TokenValidatedContext ctx = BuildContext(sp, PrincipalWith(sub: ActorId));

        await JwtBearerDenylistEvents.OnTokenValidatedAsync(ctx);

        // Fail-open for non-admins: ctx must not have failed.
        ctx.Result.Should().BeNull();

        _auditWriter.Verify(
            w => w.WriteAsync(
                It.Is<AuditEvent>(e =>
                    e.EventType == AuditEventTypes.TokenAccessDenylistUnavailable
                    && e.ActorUserId == ActorId
                    && e.PayloadJson.Contains(Jti)
                    && e.PayloadJson.Contains("user_id")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // -------------------------------------------------------------------------
    // 4. Redis outage — FGA org-admin → fail-closed
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Should_FailWithDenylistUnavailable_When_RedisOutsAndActorIsFgaOrgAdmin()
    {
        _denylist
            .Setup(d => d.IsDeniedAsync(Jti, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RedisTimeoutException("timeout", CommandStatus.Unknown));

        SetupFgaAdmin(true);
        // system_admin probe must NOT be called when FGA already returned true.

        using ServiceProvider sp = BuildServices();
        TokenValidatedContext ctx = BuildContext(sp, PrincipalWith(sub: ActorId));

        await JwtBearerDenylistEvents.OnTokenValidatedAsync(ctx);

        ctx.Result.Should().NotBeNull();
        ctx.Result!.Failure.Should().NotBeNull();
        ctx.Result.Failure!.Message.Should().Be("denylist_unavailable");

        _userLookup.Verify(
            u => u.IsSystemAdminAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // -------------------------------------------------------------------------
    // 5. Redis outage — system_admin (not FGA) → fail-closed
    //    Regression for the security-review High finding.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Should_FailClosed_When_RedisOutsAndActorIsSystemAdminButNotFgaAdmin()
    {
        _denylist
            .Setup(d => d.IsDeniedAsync(Jti, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "boom"));

        SetupFgaAdmin(false);
        SetupSystemAdmin(true);

        using ServiceProvider sp = BuildServices();
        TokenValidatedContext ctx = BuildContext(sp, PrincipalWith(sub: ActorId));

        await JwtBearerDenylistEvents.OnTokenValidatedAsync(ctx);

        ctx.Result.Should().NotBeNull();
        ctx.Result!.Failure.Should().NotBeNull();
        ctx.Result.Failure!.Message.Should().Be("denylist_unavailable");
    }

    // -------------------------------------------------------------------------
    // 6a. FGA throws, system_admin says true → fail-closed
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Should_FailClosed_When_FgaThrowsAndSystemAdminTrue()
    {
        _denylist
            .Setup(d => d.IsDeniedAsync(Jti, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "boom"));

        SetupFgaThrows(new HttpRequestException("fga down"));
        SetupSystemAdmin(true);

        using ServiceProvider sp = BuildServices();
        TokenValidatedContext ctx = BuildContext(sp, PrincipalWith(sub: ActorId));

        await JwtBearerDenylistEvents.OnTokenValidatedAsync(ctx);

        ctx.Result.Should().NotBeNull();
        ctx.Result!.Failure!.Message.Should().Be("denylist_unavailable");
    }

    // -------------------------------------------------------------------------
    // 6b. FGA throws, system_admin says false → fail-open + audit
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Should_Admit_When_FgaThrowsAndSystemAdminFalse()
    {
        _denylist
            .Setup(d => d.IsDeniedAsync(Jti, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "boom"));

        SetupFgaThrows(new HttpRequestException("fga down"));
        SetupSystemAdmin(false);
        SetupAuditWriteOk();

        using ServiceProvider sp = BuildServices();
        TokenValidatedContext ctx = BuildContext(sp, PrincipalWith(sub: ActorId));

        await JwtBearerDenylistEvents.OnTokenValidatedAsync(ctx);

        ctx.Result.Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // 7. system_admin probe throws (non-cancellation) → deny-closed
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Should_FailClosed_When_SystemAdminProbeThrows()
    {
        _denylist
            .Setup(d => d.IsDeniedAsync(Jti, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "boom"));

        SetupFgaAdmin(false);
        SetupSystemAdminThrows(new TimeoutException("db unreachable"));

        using ServiceProvider sp = BuildServices();
        TokenValidatedContext ctx = BuildContext(sp, PrincipalWith(sub: ActorId));

        await JwtBearerDenylistEvents.OnTokenValidatedAsync(ctx);

        ctx.Result.Should().NotBeNull();
        ctx.Result!.Failure!.Message.Should().Be("denylist_unavailable");
    }

    // -------------------------------------------------------------------------
    // 8a. Seed-org id unresolvable + system_admin true → fail-closed
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Should_FailClosed_When_SeedOrgUnresolvedAndSystemAdminTrue()
    {
        _seedOptions
            .SetupGet(m => m.CurrentValue)
            .Returns(new SeedOrganizationOptions { OrganizationId = Guid.Empty });

        _denylist
            .Setup(d => d.IsDeniedAsync(Jti, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "boom"));

        SetupSystemAdmin(true);
        // FGA must NOT be called when seed-org id is empty.

        using ServiceProvider sp = BuildServices();
        TokenValidatedContext ctx = BuildContext(sp, PrincipalWith(sub: ActorId));

        await JwtBearerDenylistEvents.OnTokenValidatedAsync(ctx);

        ctx.Result.Should().NotBeNull();
        ctx.Result!.Failure!.Message.Should().Be("denylist_unavailable");

        _fga.Verify(
            f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // -------------------------------------------------------------------------
    // 8b. Seed-org id unresolvable + system_admin false → fail-open
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Should_Admit_When_SeedOrgUnresolvedAndSystemAdminFalse()
    {
        _seedOptions
            .SetupGet(m => m.CurrentValue)
            .Returns(new SeedOrganizationOptions { OrganizationId = Guid.Empty });

        _denylist
            .Setup(d => d.IsDeniedAsync(Jti, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "boom"));

        SetupSystemAdmin(false);
        SetupAuditWriteOk();

        using ServiceProvider sp = BuildServices();
        TokenValidatedContext ctx = BuildContext(sp, PrincipalWith(sub: ActorId));

        await JwtBearerDenylistEvents.OnTokenValidatedAsync(ctx);

        ctx.Result.Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // 9. Non-Redis exception propagates — regression for security review Medium
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Should_PropagateException_When_DenylistThrowsNonRedisException()
    {
        _denylist
            .Setup(d => d.IsDeniedAsync(Jti, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bug"));

        using ServiceProvider sp = BuildServices();
        TokenValidatedContext ctx = BuildContext(sp, PrincipalWith(sub: ActorId));

        Func<Task> act = () => JwtBearerDenylistEvents.OnTokenValidatedAsync(ctx);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("bug");
    }

    // -------------------------------------------------------------------------
    // 10. Cancellation propagates
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Should_PropagateOperationCanceled_When_DenylistThrowsCancellation()
    {
        _denylist
            .Setup(d => d.IsDeniedAsync(Jti, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        using ServiceProvider sp = BuildServices();
        TokenValidatedContext ctx = BuildContext(sp, PrincipalWith(sub: ActorId));

        Func<Task> act = () => JwtBearerDenylistEvents.OnTokenValidatedAsync(ctx);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // -------------------------------------------------------------------------
    // No-jti claim → skip
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Should_SkipCheck_When_PrincipalHasNoJtiClaim()
    {
        // denylist is strict — must NEVER be called.
        using ServiceProvider sp = BuildServices();
        TokenValidatedContext ctx = BuildContext(sp, PrincipalWith(jti: null, sub: ActorId));

        await JwtBearerDenylistEvents.OnTokenValidatedAsync(ctx);

        ctx.Result.Should().BeNull();
        _denylist.VerifyNoOtherCalls();
    }

    // -------------------------------------------------------------------------
    // Stretch: audit payload includes jti + user_id
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Should_IncludeJtiAndUserIdInAuditPayload_When_RedisOutageAuditsNonAdmin()
    {
        AuditEvent? captured = null;
        _denylist
            .Setup(d => d.IsDeniedAsync(Jti, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "boom"));

        SetupFgaAdmin(false);
        SetupSystemAdmin(false);
        _auditWriter
            .Setup(w => w.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((e, _) => captured = e)
            .Returns(Task.CompletedTask);

        using ServiceProvider sp = BuildServices();
        TokenValidatedContext ctx = BuildContext(sp, PrincipalWith(sub: ActorId));

        await JwtBearerDenylistEvents.OnTokenValidatedAsync(ctx);

        captured.Should().NotBeNull();
        using JsonDocument doc = JsonDocument.Parse(captured!.PayloadJson);
        doc.RootElement.TryGetProperty("jti", out JsonElement jtiEl).Should().BeTrue();
        jtiEl.GetString().Should().Be(Jti);
        doc.RootElement.TryGetProperty("user_id", out JsonElement userIdEl).Should().BeTrue();
        userIdEl.GetString().Should().Be(ActorId.ToString());
    }
}
