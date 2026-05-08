using System.Collections.Generic;
using System.Security.Claims;
using FluentAssertions;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.Core.Auditing;
using Heimdall.Core.Interfaces;
using Heimdall.Web.Authorization.Policies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Heimdall.Web.Tests.Authorization.Policies;

/// <summary>
/// Unit tests for <see cref="OpenFgaAuthorizationHandler"/>. Mirrors the
/// deny-closed contract spelled out in <c>docs/proposals/openfga.md</c> §3 step
/// 9 + 10: every failure path leaves the requirement unsatisfied; the
/// break-glass path requires (a) the env-var enable flag, (b) system_admin in
/// the DB, and (c) a successful audit-write before <c>Succeed</c>.
/// </summary>
public class OpenFgaAuthorizationHandlerTests
{
    private static readonly Guid Actor = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string TicketIdValue = "42";

    private readonly Mock<IOpenFgaAuthorizationService> _fga = new(MockBehavior.Strict);
    private readonly Mock<IUserLookup> _userLookup = new(MockBehavior.Loose);
    private readonly Mock<IAuditEventWriter> _auditWriter = new(MockBehavior.Loose);
    private readonly Mock<IHttpContextAccessor> _httpAccessor = new();
    private IConfiguration _config = new ConfigurationBuilder().Build();

    private OpenFgaAuthorizationHandler CreateSut() =>
        new(
            _fga.Object,
            _userLookup.Object,
            _auditWriter.Object,
            _httpAccessor.Object,
            _config,
            NullLogger<OpenFgaAuthorizationHandler>.Instance);

    private static OpenFgaRequirement TicketViewReq() => new("ticket", "view", "ticketId");

    private static AuthorizationHandlerContext CreateContext(
        OpenFgaRequirement requirement,
        ClaimsPrincipal? user = null)
    {
        return new AuthorizationHandlerContext(
            new[] { requirement },
            user ?? new ClaimsPrincipal(new ClaimsIdentity()),
            resource: null);
    }

    private static ClaimsPrincipal Principal(string? nameIdentifier)
    {
        var claims = nameIdentifier is null
            ? Array.Empty<Claim>()
            : new[] { new Claim(ClaimTypes.NameIdentifier, nameIdentifier) };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private void SetHttpContextWithRoute(
        Dictionary<string, object?>? routeValues = null,
        Dictionary<string, string>? queryValues = null)
    {
        var http = new DefaultHttpContext();
        if (routeValues is not null)
        {
            foreach (var kv in routeValues)
            {
                http.Request.RouteValues[kv.Key] = kv.Value;
            }
        }

        if (queryValues is not null)
        {
            http.Request.QueryString = new QueryString(
                "?" + string.Join("&", queryValues.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        _httpAccessor.SetupGet(a => a.HttpContext).Returns(http);
    }

    private void SetBreakGlass(string? value)
    {
        var dict = new Dictionary<string, string?>();
        if (value is not null)
        {
            dict[OpenFgaAuthorizationHandler.BreakGlassConfigKey] = value;
        }

        _config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    // ─── Constructor guards ──────────────────────────────────────────

    [Fact]
    public void Constructor_Should_Throw_When_AnyDependencyIsNull()
    {
        Action a = () => new OpenFgaAuthorizationHandler(null!, _userLookup.Object, _auditWriter.Object, _httpAccessor.Object, _config, NullLogger<OpenFgaAuthorizationHandler>.Instance);
        Action b = () => new OpenFgaAuthorizationHandler(_fga.Object, null!, _auditWriter.Object, _httpAccessor.Object, _config, NullLogger<OpenFgaAuthorizationHandler>.Instance);
        Action c = () => new OpenFgaAuthorizationHandler(_fga.Object, _userLookup.Object, null!, _httpAccessor.Object, _config, NullLogger<OpenFgaAuthorizationHandler>.Instance);
        Action d = () => new OpenFgaAuthorizationHandler(_fga.Object, _userLookup.Object, _auditWriter.Object, null!, _config, NullLogger<OpenFgaAuthorizationHandler>.Instance);
        Action e = () => new OpenFgaAuthorizationHandler(_fga.Object, _userLookup.Object, _auditWriter.Object, _httpAccessor.Object, null!, NullLogger<OpenFgaAuthorizationHandler>.Instance);
        Action f = () => new OpenFgaAuthorizationHandler(_fga.Object, _userLookup.Object, _auditWriter.Object, _httpAccessor.Object, _config, null!);

        a.Should().Throw<ArgumentNullException>();
        b.Should().Throw<ArgumentNullException>();
        c.Should().Throw<ArgumentNullException>();
        d.Should().Throw<ArgumentNullException>();
        e.Should().Throw<ArgumentNullException>();
        f.Should().Throw<ArgumentNullException>();
    }

    // ─── Happy / deny paths ──────────────────────────────────────────

    [Fact]
    public async Task Should_Succeed_When_FgaCheckAllows()
    {
        SetHttpContextWithRoute(new() { ["ticketId"] = TicketIdValue });
        _fga
            .Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var req = TicketViewReq();
        var ctx = CreateContext(req, Principal(Actor.ToString()));

        await CreateSut().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task Should_NotSucceed_When_FgaDeniesAndBreakGlassDisabled()
    {
        SetHttpContextWithRoute(new() { ["ticketId"] = TicketIdValue });
        _fga
            .Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var req = TicketViewReq();
        var ctx = CreateContext(req, Principal(Actor.ToString()));

        await CreateSut().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Should_NotSucceed_When_NameIdentifierClaimMissing()
    {
        SetHttpContextWithRoute(new() { ["ticketId"] = TicketIdValue });
        var req = TicketViewReq();
        var ctx = CreateContext(req, Principal(null));

        await CreateSut().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
        _fga.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Should_NotSucceed_When_NameIdentifierUnparseable()
    {
        SetHttpContextWithRoute(new() { ["ticketId"] = TicketIdValue });
        var req = TicketViewReq();
        var ctx = CreateContext(req, Principal("not-a-guid"));

        await CreateSut().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
        _fga.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Should_NotSucceed_When_RouteValueMissing()
    {
        SetHttpContextWithRoute(routeValues: new());
        var req = TicketViewReq();
        var ctx = CreateContext(req, Principal(Actor.ToString()));

        await CreateSut().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
        _fga.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Should_FallBackToQueryString_When_RouteValueMissing()
    {
        SetHttpContextWithRoute(
            routeValues: new(),
            queryValues: new() { ["ticketId"] = TicketIdValue });
        _fga
            .Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var req = TicketViewReq();
        var ctx = CreateContext(req, Principal(Actor.ToString()));

        await CreateSut().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task Should_CallFgaWithExpectedTuple()
    {
        SetHttpContextWithRoute(new() { ["ticketId"] = TicketIdValue });
        FgaCheckRequest? captured = null;
        _fga
            .Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
            .Callback<FgaCheckRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(true);

        var req = TicketViewReq();
        var ctx = CreateContext(req, Principal(Actor.ToString()));

        await CreateSut().HandleAsync(ctx);

        captured.Should().NotBeNull();
        captured!.User.Should().Be(TupleShapes.UserRef(Actor));
        captured.Relation.Should().Be("view");
        captured.Object.Should().Be("ticket:" + TicketIdValue);
        captured.Consistency.Should().Be(FgaConsistency.MinimizeLatency);
    }

    [Fact]
    public async Task Should_PropagateOperationCanceled_When_FgaCancels()
    {
        SetHttpContextWithRoute(new() { ["ticketId"] = TicketIdValue });
        _fga
            .Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var req = TicketViewReq();
        var ctx = CreateContext(req, Principal(Actor.ToString()));

        Func<Task> act = () => CreateSut().HandleAsync(ctx);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Should_DenyClosed_When_FgaThrowsAndBreakGlassDisabled()
    {
        SetHttpContextWithRoute(new() { ["ticketId"] = TicketIdValue });
        _fga
            .Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("sidecar boom"));

        var req = TicketViewReq();
        var ctx = CreateContext(req, Principal(Actor.ToString()));

        await CreateSut().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
    }

    // ─── Break-glass paths ───────────────────────────────────────────

    [Fact]
    public async Task Should_BreakGlass_When_EnvOnAndSystemAdminAndAuditSucceeds()
    {
        SetHttpContextWithRoute(new() { ["ticketId"] = TicketIdValue });
        SetBreakGlass("1");
        _fga
            .Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _userLookup
            .Setup(u => u.IsSystemAdminAsync(Actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var req = TicketViewReq();
        var ctx = CreateContext(req, Principal(Actor.ToString()));

        await CreateSut().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue();
        _auditWriter.Verify(
            w => w.WriteAsync(
                It.Is<AuditEvent>(e =>
                    e.EventType == OpenFgaAuthorizationHandler.BreakGlassAuditEventType
                    && e.ActorUserId == Actor
                    && e.Target == "ticket:" + TicketIdValue
                    && e.PayloadJson.Contains("ticket:" + TicketIdValue)
                    && e.PayloadJson.Contains("\"relation\":\"view\"")
                    && e.PayloadJson.Contains("\"object_type\":\"ticket\"")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    public async Task Should_BreakGlass_When_EnvIsCaseInsensitiveTrue(string value)
    {
        SetHttpContextWithRoute(new() { ["ticketId"] = TicketIdValue });
        SetBreakGlass(value);
        _fga
            .Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _userLookup
            .Setup(u => u.IsSystemAdminAsync(Actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var ctx = CreateContext(TicketViewReq(), Principal(Actor.ToString()));

        await CreateSut().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("")]
    public async Task Should_NotBreakGlass_When_EnvIsNotTruthy(string envValue)
    {
        SetHttpContextWithRoute(new() { ["ticketId"] = TicketIdValue });
        SetBreakGlass(string.IsNullOrEmpty(envValue) ? null : envValue);
        _fga
            .Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var ctx = CreateContext(TicketViewReq(), Principal(Actor.ToString()));

        await CreateSut().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
        _userLookup.Verify(
            u => u.IsSystemAdminAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Should_NotBreakGlass_When_NotSystemAdmin()
    {
        SetHttpContextWithRoute(new() { ["ticketId"] = TicketIdValue });
        SetBreakGlass("1");
        _fga
            .Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _userLookup
            .Setup(u => u.IsSystemAdminAsync(Actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var ctx = CreateContext(TicketViewReq(), Principal(Actor.ToString()));

        await CreateSut().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
        _auditWriter.Verify(
            w => w.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Should_NotBreakGlass_When_AuditWriteThrows()
    {
        SetHttpContextWithRoute(new() { ["ticketId"] = TicketIdValue });
        SetBreakGlass("1");
        _fga
            .Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _userLookup
            .Setup(u => u.IsSystemAdminAsync(Actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _auditWriter
            .Setup(w => w.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("audit table down"));

        var ctx = CreateContext(TicketViewReq(), Principal(Actor.ToString()));

        await CreateSut().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Should_DenyClosed_When_BreakGlassLookupThrows()
    {
        SetHttpContextWithRoute(new() { ["ticketId"] = TicketIdValue });
        SetBreakGlass("1");
        _fga
            .Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _userLookup
            .Setup(u => u.IsSystemAdminAsync(Actor, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var ctx = CreateContext(TicketViewReq(), Principal(Actor.ToString()));

        await CreateSut().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
    }
}
