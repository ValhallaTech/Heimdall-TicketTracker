using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Heimdall.BLL.Tests.Authorization.OpenFga;

/// <summary>
/// Unit tests for <see cref="OpenFgaPermissionService"/> — the Phase 3.5
/// <see cref="IPermissionService"/> implementation that fans out to OpenFGA
/// <c>Check</c>. Each test asserts (a) the system-admin short-circuit, (b) the
/// exact <see cref="FgaCheckRequest"/> shape forwarded to the adapter, (c) the
/// deny-closed catch-all, and (d) cooperative cancellation propagation.
/// </summary>
public class OpenFgaPermissionServiceTests
{
    private static readonly Guid ActorId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TeamId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TargetId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid OtherTeamId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static (OpenFgaPermissionService Sut, Mock<IOpenFgaAuthorizationService> Fga, Mock<IUserLookup> Users)
        CreateSut(MockBehavior fgaBehaviour = MockBehavior.Strict)
    {
        var fga = new Mock<IOpenFgaAuthorizationService>(fgaBehaviour);
        var users = new Mock<IUserLookup>(MockBehavior.Strict);
        users.Setup(u => u.IsSystemAdminAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(false);
        var sut = new OpenFgaPermissionService(
            fga.Object,
            users.Object,
            NullLogger<OpenFgaPermissionService>.Instance);
        return (sut, fga, users);
    }

    private static Ticket BuildTicket(int id = 42, Guid? assigneeId = null) =>
        new()
        {
            Id = id,
            ProjectId = Guid.NewGuid(),
            TeamId = TeamId,
            ReporterId = ActorId,
            AssigneeId = assigneeId,
        };

    // ─── Constructor guards ───────────────────────────────────────────────

    [Fact]
    public void Ctor_Should_Throw_When_FgaIsNull()
    {
        Action act = () => new OpenFgaPermissionService(
            null!,
            Mock.Of<IUserLookup>(),
            NullLogger<OpenFgaPermissionService>.Instance);

        act.Should().Throw<ArgumentNullException>().WithParameterName("fga");
    }

    [Fact]
    public void Ctor_Should_Throw_When_UserLookupIsNull()
    {
        Action act = () => new OpenFgaPermissionService(
            Mock.Of<IOpenFgaAuthorizationService>(),
            null!,
            NullLogger<OpenFgaPermissionService>.Instance);

        act.Should().Throw<ArgumentNullException>().WithParameterName("userLookup");
    }

    [Fact]
    public void Ctor_Should_Throw_When_LoggerIsNull()
    {
        Action act = () => new OpenFgaPermissionService(
            Mock.Of<IOpenFgaAuthorizationService>(),
            Mock.Of<IUserLookup>(),
            null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    // ─── CanViewTeamQueueAsync ────────────────────────────────────────────

    [Fact]
    public async Task CanViewTeamQueueAsync_Should_ShortCircuitTrue_When_ActorIsSystemAdmin()
    {
        var (sut, fga, users) = CreateSut();
        users.Setup(u => u.IsSystemAdminAsync(ActorId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(true);

        var result = await sut.CanViewTeamQueueAsync(ActorId, TeamId, CancellationToken.None);

        result.Should().BeTrue();
        fga.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CanViewTeamQueueAsync_Should_SendTeamViewCheck_When_NotSystemAdmin()
    {
        var (sut, fga, _) = CreateSut();
        FgaCheckRequest? captured = null;
        fga.Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
           .Callback<FgaCheckRequest, CancellationToken>((r, _) => captured = r)
           .ReturnsAsync(true);

        var result = await sut.CanViewTeamQueueAsync(ActorId, TeamId, CancellationToken.None);

        result.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.User.Should().Be(TupleShapes.UserRef(ActorId));
        captured.Relation.Should().Be("view");
        captured.Object.Should().Be(TupleShapes.TeamRef(TeamId));
        captured.Consistency.Should().Be(FgaConsistency.MinimizeLatency);
    }

    [Fact]
    public async Task CanViewTeamQueueAsync_Should_ReturnFalse_When_FgaDenies()
    {
        var (sut, fga, _) = CreateSut();
        fga.Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(false);

        var result = await sut.CanViewTeamQueueAsync(ActorId, TeamId, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanViewTeamQueueAsync_Should_DenyClosed_When_FgaThrows()
    {
        var (sut, fga, _) = CreateSut();
        fga.Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await sut.CanViewTeamQueueAsync(ActorId, TeamId, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanViewTeamQueueAsync_Should_PropagateOperationCanceled()
    {
        var (sut, fga, _) = CreateSut();
        fga.Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new OperationCanceledException());

        Func<Task> act = () => sut.CanViewTeamQueueAsync(ActorId, TeamId, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ─── CanRouteTicketAsync ──────────────────────────────────────────────

    [Fact]
    public async Task CanRouteTicketAsync_Should_Throw_When_TicketIsNull()
    {
        var (sut, _, _) = CreateSut();

        Func<Task> act = () => sut.CanRouteTicketAsync(ActorId, null!, OtherTeamId, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("ticket");
    }

    [Fact]
    public async Task CanRouteTicketAsync_Should_ShortCircuitTrue_When_ActorIsSystemAdmin()
    {
        var (sut, fga, users) = CreateSut();
        users.Setup(u => u.IsSystemAdminAsync(ActorId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(true);

        var result = await sut.CanRouteTicketAsync(ActorId, BuildTicket(), OtherTeamId, CancellationToken.None);

        result.Should().BeTrue();
        fga.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CanRouteTicketAsync_Should_SendTicketAssignCheck_When_NotSystemAdmin()
    {
        var (sut, fga, _) = CreateSut();
        FgaCheckRequest? captured = null;
        fga.Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
           .Callback<FgaCheckRequest, CancellationToken>((r, _) => captured = r)
           .ReturnsAsync(true);
        Ticket ticket = BuildTicket(id: 7);

        var result = await sut.CanRouteTicketAsync(ActorId, ticket, OtherTeamId, CancellationToken.None);

        result.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.User.Should().Be(TupleShapes.UserRef(ActorId));
        captured.Relation.Should().Be("assign");
        captured.Object.Should().Be(TupleShapes.TicketRef(7));
    }

    [Fact]
    public async Task CanRouteTicketAsync_Should_DenyClosed_When_FgaThrows()
    {
        var (sut, fga, _) = CreateSut();
        fga.Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await sut.CanRouteTicketAsync(ActorId, BuildTicket(), OtherTeamId, CancellationToken.None);

        result.Should().BeFalse();
    }

    // ─── CanAssignTicketAsync ─────────────────────────────────────────────

    [Fact]
    public async Task CanAssignTicketAsync_Should_Throw_When_TicketIsNull()
    {
        var (sut, _, _) = CreateSut();

        Func<Task> act = () => sut.CanAssignTicketAsync(ActorId, null!, TargetId, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("ticket");
    }

    [Fact]
    public async Task CanAssignTicketAsync_Should_ShortCircuitTrue_When_ActorIsSystemAdmin()
    {
        var (sut, fga, users) = CreateSut();
        users.Setup(u => u.IsSystemAdminAsync(ActorId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(true);

        var result = await sut.CanAssignTicketAsync(ActorId, BuildTicket(), TargetId, CancellationToken.None);

        result.Should().BeTrue();
        fga.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CanAssignTicketAsync_Should_ReturnTrue_When_FgaAssignAllows()
    {
        var (sut, fga, _) = CreateSut();
        FgaCheckRequest? captured = null;
        fga.Setup(f => f.CheckAsync(
                It.Is<FgaCheckRequest>(r => r.Relation == "assign"),
                It.IsAny<CancellationToken>()))
           .Callback<FgaCheckRequest, CancellationToken>((r, _) => captured = r)
           .ReturnsAsync(true);

        var result = await sut.CanAssignTicketAsync(
            ActorId,
            BuildTicket(id: 9),
            TargetId,
            CancellationToken.None);

        result.Should().BeTrue();
        captured!.User.Should().Be(TupleShapes.UserRef(ActorId));
        captured.Object.Should().Be(TupleShapes.TicketRef(9));
        // No second call needed when the direct assign path already grants.
        fga.Verify(
            f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CanAssignTicketAsync_Should_AllowSelfClaim_When_TicketUnassignedAndActorHasView()
    {
        // Self-claim parity carve-out (§5.3): plain project member self-claiming
        // an unassigned ticket. FGA denies `assign` but grants `view`.
        var (sut, fga, _) = CreateSut();
        fga.Setup(f => f.CheckAsync(
                It.Is<FgaCheckRequest>(r => r.Relation == "assign"),
                It.IsAny<CancellationToken>()))
           .ReturnsAsync(false);
        fga.Setup(f => f.CheckAsync(
                It.Is<FgaCheckRequest>(r => r.Relation == "view"),
                It.IsAny<CancellationToken>()))
           .ReturnsAsync(true);

        var result = await sut.CanAssignTicketAsync(
            ActorId,
            BuildTicket(assigneeId: null),
            targetUserId: ActorId, // self
            CancellationToken.None);

        result.Should().BeTrue();
        fga.Verify(
            f => f.CheckAsync(It.Is<FgaCheckRequest>(r => r.Relation == "view"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CanAssignTicketAsync_Should_DenySelfClaim_When_TicketAlreadyAssigned()
    {
        var (sut, fga, _) = CreateSut();
        fga.Setup(f => f.CheckAsync(
                It.Is<FgaCheckRequest>(r => r.Relation == "assign"),
                It.IsAny<CancellationToken>()))
           .ReturnsAsync(false);

        // Ticket is already assigned — self-claim carve-out does not apply,
        // so the view fallback must NOT be issued.
        var result = await sut.CanAssignTicketAsync(
            ActorId,
            BuildTicket(assigneeId: TargetId),
            targetUserId: ActorId,
            CancellationToken.None);

        result.Should().BeFalse();
        fga.Verify(
            f => f.CheckAsync(It.Is<FgaCheckRequest>(r => r.Relation == "view"), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CanAssignTicketAsync_Should_DenySelfClaim_When_AssigningSomeoneElse()
    {
        var (sut, fga, _) = CreateSut();
        fga.Setup(f => f.CheckAsync(
                It.Is<FgaCheckRequest>(r => r.Relation == "assign"),
                It.IsAny<CancellationToken>()))
           .ReturnsAsync(false);

        // targetUserId != actorId — the self-claim carve-out must not fire.
        var result = await sut.CanAssignTicketAsync(
            ActorId,
            BuildTicket(assigneeId: null),
            targetUserId: TargetId,
            CancellationToken.None);

        result.Should().BeFalse();
        fga.Verify(
            f => f.CheckAsync(It.Is<FgaCheckRequest>(r => r.Relation == "view"), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CanAssignTicketAsync_Should_DenyClosed_When_FgaThrows()
    {
        var (sut, fga, _) = CreateSut();
        fga.Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await sut.CanAssignTicketAsync(
            ActorId,
            BuildTicket(),
            TargetId,
            CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanAssignTicketAsync_Should_PropagateOperationCanceled()
    {
        var (sut, fga, _) = CreateSut();
        fga.Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new OperationCanceledException());

        Func<Task> act = () => sut.CanAssignTicketAsync(
            ActorId,
            BuildTicket(),
            TargetId,
            CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ─── CanManageTeamMembersAsync ────────────────────────────────────────

    [Fact]
    public async Task CanManageTeamMembersAsync_Should_ShortCircuitTrue_When_ActorIsSystemAdmin()
    {
        var (sut, fga, users) = CreateSut();
        users.Setup(u => u.IsSystemAdminAsync(ActorId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(true);

        var result = await sut.CanManageTeamMembersAsync(ActorId, TeamId, CancellationToken.None);

        result.Should().BeTrue();
        fga.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CanManageTeamMembersAsync_Should_SendTeamManageMembersCheck()
    {
        var (sut, fga, _) = CreateSut();
        FgaCheckRequest? captured = null;
        fga.Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
           .Callback<FgaCheckRequest, CancellationToken>((r, _) => captured = r)
           .ReturnsAsync(true);

        var result = await sut.CanManageTeamMembersAsync(ActorId, TeamId, CancellationToken.None);

        result.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.User.Should().Be(TupleShapes.UserRef(ActorId));
        captured.Relation.Should().Be("manage_members");
        captured.Object.Should().Be(TupleShapes.TeamRef(TeamId));
    }

    [Fact]
    public async Task CanManageTeamMembersAsync_Should_DenyClosed_When_FgaThrows()
    {
        var (sut, fga, _) = CreateSut();
        fga.Setup(f => f.CheckAsync(It.IsAny<FgaCheckRequest>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await sut.CanManageTeamMembersAsync(ActorId, TeamId, CancellationToken.None);

        result.Should().BeFalse();
    }
}
