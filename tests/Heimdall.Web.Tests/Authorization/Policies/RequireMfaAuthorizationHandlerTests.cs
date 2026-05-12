using System.Security.Claims;
using FluentAssertions;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.Core.Auditing;
using Heimdall.Core.Interfaces;
using Heimdall.Web.Authorization.Policies;
using Heimdall.Web.Bootstrap;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Heimdall.Web.Tests.Authorization.Policies;

/// <summary>
/// Unit tests for <see cref="RequireMfaAuthorizationHandler"/>. Pins the Phase
/// 4.6 step 16 contract: non-admins succeed; admins require both
/// <c>amr=mfa</c> AND a live <c>two_factor_enabled</c> column; break-glass
/// requires env flag + <c>system_admin</c> + successful audit write.
/// </summary>
public class RequireMfaAuthorizationHandlerTests
{
    private static readonly Guid Actor = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SeedOrg = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly Mock<IOpenFgaAuthorizationService> _fga = new(MockBehavior.Strict);
    private readonly Mock<IUserLookup> _userLookup = new(MockBehavior.Loose);
    private readonly Mock<IAuditEventWriter> _auditWriter = new(MockBehavior.Loose);
    private readonly Mock<IHttpContextAccessor> _httpAccessor = new();
    private readonly Mock<IOptionsMonitor<SeedOrganizationOptions>> _seedOptions = new();
    private IConfiguration _config = new ConfigurationBuilder().Build();

    public RequireMfaAuthorizationHandlerTests()
    {
        _seedOptions.SetupGet(m => m.CurrentValue)
                    .Returns(new SeedOrganizationOptions { OrganizationId = SeedOrg });
        _httpAccessor.SetupGet(a => a.HttpContext).Returns(new DefaultHttpContext());
    }

    private RequireMfaAuthorizationHandler CreateSut() =>
        new(
            _fga.Object,
            _userLookup.Object,
            _auditWriter.Object,
            _httpAccessor.Object,
            _config,
            _seedOptions.Object,
            NullLogger<RequireMfaAuthorizationHandler>.Instance);

    private static AuthorizationHandlerContext CreateContext(ClaimsPrincipal user) =>
        new(new[] { (IAuthorizationRequirement)new RequireMfaRequirement() }, user, resource: null);

    private static ClaimsPrincipal Principal(Guid? id, bool withMfaAmr)
    {
        var claims = new List<Claim>();
        if (id is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, id.Value.ToString()));
        }

        if (withMfaAmr)
        {
            claims.Add(new Claim(RequireMfaAuthorizationHandler.AmrClaimType, RequireMfaAuthorizationHandler.MfaAmrValue));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private void EnableBreakGlass()
    {
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OpenFgaAuthorizationHandler.BreakGlassConfigKey] = "true",
            })
            .Build();
    }

    private void SetupFgaAdmin(bool isAdmin)
    {
        _fga.Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(isAdmin);
    }

    [Fact]
    public void Constructor_Should_Throw_When_AnyDependencyIsNull()
    {
        Action a = () => new RequireMfaAuthorizationHandler(null!, _userLookup.Object, _auditWriter.Object, _httpAccessor.Object, _config, _seedOptions.Object, NullLogger<RequireMfaAuthorizationHandler>.Instance);
        Action b = () => new RequireMfaAuthorizationHandler(_fga.Object, null!, _auditWriter.Object, _httpAccessor.Object, _config, _seedOptions.Object, NullLogger<RequireMfaAuthorizationHandler>.Instance);
        Action c = () => new RequireMfaAuthorizationHandler(_fga.Object, _userLookup.Object, null!, _httpAccessor.Object, _config, _seedOptions.Object, NullLogger<RequireMfaAuthorizationHandler>.Instance);
        Action d = () => new RequireMfaAuthorizationHandler(_fga.Object, _userLookup.Object, _auditWriter.Object, null!, _config, _seedOptions.Object, NullLogger<RequireMfaAuthorizationHandler>.Instance);
        Action e = () => new RequireMfaAuthorizationHandler(_fga.Object, _userLookup.Object, _auditWriter.Object, _httpAccessor.Object, null!, _seedOptions.Object, NullLogger<RequireMfaAuthorizationHandler>.Instance);
        Action f = () => new RequireMfaAuthorizationHandler(_fga.Object, _userLookup.Object, _auditWriter.Object, _httpAccessor.Object, _config, null!, NullLogger<RequireMfaAuthorizationHandler>.Instance);
        Action g = () => new RequireMfaAuthorizationHandler(_fga.Object, _userLookup.Object, _auditWriter.Object, _httpAccessor.Object, _config, _seedOptions.Object, null!);

        a.Should().Throw<ArgumentNullException>();
        b.Should().Throw<ArgumentNullException>();
        c.Should().Throw<ArgumentNullException>();
        d.Should().Throw<ArgumentNullException>();
        e.Should().Throw<ArgumentNullException>();
        f.Should().Throw<ArgumentNullException>();
        g.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_NotSucceed_When_NameIdentifierUnparseable()
    {
        var ctx = CreateContext(Principal(id: null, withMfaAmr: false));

        await CreateSut().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
        _fga.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Should_NotSucceed_When_SeedOrgIdUnresolved()
    {
        _seedOptions.SetupGet(m => m.CurrentValue)
                    .Returns(new SeedOrganizationOptions { OrganizationId = Guid.Empty });
        var ctx = CreateContext(Principal(Actor, withMfaAmr: true));

        await CreateSut().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
        _fga.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Should_Succeed_When_ActorIsNotOrgAdminAndNotSystemAdmin()
    {
        SetupFgaAdmin(false);
        _userLookup.Setup(u => u.IsSystemAdminAsync(Actor, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(false);
        var ctx = CreateContext(Principal(Actor, withMfaAmr: false));

        await CreateSut().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task Should_Succeed_When_ActorIsAdminWithAmrAndTwoFactorEnabled()
    {
        SetupFgaAdmin(true);
        _userLookup.Setup(u => u.IsTwoFactorEnabledAsync(Actor, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(true);

        var ctx = CreateContext(Principal(Actor, withMfaAmr: true));

        await CreateSut().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task Should_NotSucceed_When_AdminMissingAmrClaim()
    {
        SetupFgaAdmin(true);
        _userLookup.Setup(u => u.IsTwoFactorEnabledAsync(Actor, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(true);

        var ctx = CreateContext(Principal(Actor, withMfaAmr: false));

        await CreateSut().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Should_NotSucceed_When_AdminHasAmrButTwoFactorDisabledInDb()
    {
        SetupFgaAdmin(true);
        _userLookup.Setup(u => u.IsTwoFactorEnabledAsync(Actor, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(false);

        var ctx = CreateContext(Principal(Actor, withMfaAmr: true));

        await CreateSut().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Should_NotSucceed_When_FgaUnavailableAndActorIsSystemAdminMissingMfa()
    {
        // FGA transport failure surfaces as `false` from the adapter (deny-closed
        // contract). For RequireMfa specifically, that must NOT silently grant
        // — a system_admin missing MFA must be deny-closed because the chained
        // SystemAdmin policy is a DB-only ALLOW (not a deny) and would otherwise
        // let them through to /admin/* on outage. Phase 4.6 step 16 sub-bullet 5.
        _fga.Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transport"));
        _userLookup.Setup(u => u.IsSystemAdminAsync(Actor, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(true);
        _userLookup.Setup(u => u.IsTwoFactorEnabledAsync(Actor, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(false);

        var ctx = CreateContext(Principal(Actor, withMfaAmr: false));

        await CreateSut().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Should_Succeed_When_FgaUnavailableAndActorIsNotSystemAdmin()
    {
        // The same FGA transport failure for a confirmed non-privileged actor
        // (system_admin = false) takes the "succeed unconditionally" branch —
        // the spec's "non-admins are not subject to MFA" invariant is preserved
        // for the population that demonstrably does not need it.
        _fga.Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transport"));
        _userLookup.Setup(u => u.IsSystemAdminAsync(Actor, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(false);

        var ctx = CreateContext(Principal(Actor, withMfaAmr: false));

        await CreateSut().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task Should_NotSucceed_When_SystemAdminProbeThrows()
    {
        // If the DB-only system_admin probe itself fails, we cannot determine
        // whether the actor is privileged. Deny-closed.
        SetupFgaAdmin(false);
        _userLookup.Setup(u => u.IsSystemAdminAsync(Actor, It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new InvalidOperationException("db down"));

        var ctx = CreateContext(Principal(Actor, withMfaAmr: false));

        await CreateSut().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Should_NotSucceed_When_AdminMissingMfaAndNotSystemAdminWithBreakGlassEnabled()
    {
        EnableBreakGlass();
        SetupFgaAdmin(true);
        _userLookup.Setup(u => u.IsTwoFactorEnabledAsync(Actor, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(false);
        _userLookup.Setup(u => u.IsSystemAdminAsync(Actor, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(false);

        var ctx = CreateContext(Principal(Actor, withMfaAmr: false));

        await CreateSut().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
        _auditWriter.Verify(a => a.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Should_Succeed_When_BreakGlassEnabledAndSystemAdminAndAuditWriteSucceeds()
    {
        EnableBreakGlass();
        SetupFgaAdmin(true);
        _userLookup.Setup(u => u.IsTwoFactorEnabledAsync(Actor, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(false);
        _userLookup.Setup(u => u.IsSystemAdminAsync(Actor, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(true);
        _auditWriter.Setup(a => a.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);

        var ctx = CreateContext(Principal(Actor, withMfaAmr: false));

        await CreateSut().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue();
        _auditWriter.Verify(
            a => a.WriteAsync(
                It.Is<AuditEvent>(e => e.EventType == RequireMfaAuthorizationHandler.MfaBreakGlassAuditEventType),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_NotSucceed_When_BreakGlassSystemAdminButAuditWriteFails()
    {
        EnableBreakGlass();
        SetupFgaAdmin(true);
        _userLookup.Setup(u => u.IsTwoFactorEnabledAsync(Actor, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(false);
        _userLookup.Setup(u => u.IsSystemAdminAsync(Actor, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(true);
        _auditWriter.Setup(a => a.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new InvalidOperationException("audit sink offline"));

        var ctx = CreateContext(Principal(Actor, withMfaAmr: false));

        await CreateSut().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Should_CallFgaWithSeedOrgAdminTupleAndHigherConsistency()
    {
        SetupFgaAdmin(false);
        FgaCheckRequest? captured = null;
        _fga.Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
            .Callback<FgaCheckRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(false);

        var ctx = CreateContext(Principal(Actor, withMfaAmr: false));

        await CreateSut().HandleAsync(ctx);

        captured.Should().NotBeNull();
        captured!.User.Should().Be(TupleShapes.UserRef(Actor));
        captured.Relation.Should().Be(TupleShapes.AdminRelation);
        captured.Object.Should().Be(TupleShapes.OrganizationRef(SeedOrg));
        captured.Consistency.Should().Be(FgaConsistency.HigherConsistency);
    }
}
