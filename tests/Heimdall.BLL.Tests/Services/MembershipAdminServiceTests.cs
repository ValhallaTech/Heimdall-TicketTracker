using System.Threading;
using FluentAssertions;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.BLL.Services;
using Heimdall.Core.Auditing;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Heimdall.BLL.Tests.Services;

/// <summary>
/// Unit tests for <see cref="MembershipAdminService"/>. Pins the DB → audit →
/// tuple ordering and the snake-case audit payload shape spelled out in
/// <c>docs/proposals/openfga.md</c> §3 step 11.
/// </summary>
public class MembershipAdminServiceTests
{
    private static readonly Guid Org = Guid.Parse("aaaa1111-1111-1111-1111-111111111111");
    private static readonly Guid Team = Guid.Parse("bbbb2222-2222-2222-2222-222222222222");
    private static readonly Guid Project = Guid.Parse("cccc3333-3333-3333-3333-333333333333");
    private static readonly Guid User = Guid.Parse("dddd4444-4444-4444-4444-444444444444");
    private static readonly Guid Actor = Guid.Parse("eeee5555-5555-5555-5555-555555555555");

    private readonly Mock<IOrganizationMemberRepository> _orgRepo = new(MockBehavior.Strict);
    private readonly Mock<ITeamMemberRepository> _teamRepo = new(MockBehavior.Strict);
    private readonly Mock<IProjectMemberRepository> _projectRepo = new(MockBehavior.Strict);
    private readonly Mock<IAuditEventWriter> _audit = new(MockBehavior.Strict);
    private readonly Mock<ITupleWriter> _tupleWriter = new(MockBehavior.Strict);

    private MembershipAdminService CreateSut() => new(
        _orgRepo.Object,
        _teamRepo.Object,
        _projectRepo.Object,
        _audit.Object,
        _tupleWriter.Object,
        NullLogger<MembershipAdminService>.Instance);

    // ─── Constructor guards ──────────────────────────────────────────

    [Fact]
    public void Constructor_Should_Throw_When_AnyDependencyIsNull()
    {
        Action a = () => new MembershipAdminService(null!, _teamRepo.Object, _projectRepo.Object, _audit.Object, _tupleWriter.Object, NullLogger<MembershipAdminService>.Instance);
        Action b = () => new MembershipAdminService(_orgRepo.Object, null!, _projectRepo.Object, _audit.Object, _tupleWriter.Object, NullLogger<MembershipAdminService>.Instance);
        Action c = () => new MembershipAdminService(_orgRepo.Object, _teamRepo.Object, null!, _audit.Object, _tupleWriter.Object, NullLogger<MembershipAdminService>.Instance);
        Action d = () => new MembershipAdminService(_orgRepo.Object, _teamRepo.Object, _projectRepo.Object, null!, _tupleWriter.Object, NullLogger<MembershipAdminService>.Instance);
        Action e = () => new MembershipAdminService(_orgRepo.Object, _teamRepo.Object, _projectRepo.Object, _audit.Object, null!, NullLogger<MembershipAdminService>.Instance);
        Action f = () => new MembershipAdminService(_orgRepo.Object, _teamRepo.Object, _projectRepo.Object, _audit.Object, _tupleWriter.Object, null!);

        a.Should().Throw<ArgumentNullException>();
        b.Should().Throw<ArgumentNullException>();
        c.Should().Throw<ArgumentNullException>();
        d.Should().Throw<ArgumentNullException>();
        e.Should().Throw<ArgumentNullException>();
        f.Should().Throw<ArgumentNullException>();
    }

    // ─── AddOrgMemberAsync ───────────────────────────────────────────

    [Fact]
    public async Task AddOrgMemberAsync_Should_WriteRowAuditAndTuple()
    {
        var sequence = new MockSequence();
        _orgRepo.InSequence(sequence)
            .Setup(r => r.AddAsync(It.IsAny<OrganizationMember>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        AuditEvent? captured = null;
        _audit.InSequence(sequence)
            .Setup(a => a.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((e, _) => captured = e)
            .Returns(Task.CompletedTask);
        _tupleWriter.InSequence(sequence)
            .Setup(t => t.WriteAsync(It.IsAny<TupleKey>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await CreateSut().AddOrgMemberAsync(Org, User, "Admin", Actor);

        _orgRepo.Verify(r => r.AddAsync(
            It.Is<OrganizationMember>(m => m.UserId == User && m.OrganizationId == Org && m.Role == "admin"),
            It.IsAny<CancellationToken>()), Times.Once);
        captured.Should().NotBeNull();
        captured!.EventType.Should().Be("membership_added");
        captured.ActorUserId.Should().Be(Actor);
        captured.Target.Should().Be(Org.ToString("D"));
        captured.PayloadJson.Should().Contain("\"scope\":\"organization\"");
        captured.PayloadJson.Should().Contain("\"role\":\"admin\"");
        _tupleWriter.Verify(t => t.WriteAsync(It.IsAny<TupleKey>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("admin")]
    [InlineData("MEMBER")]
    [InlineData("viewer")]
    public async Task AddOrgMemberAsync_Should_NormalizeRoleToLowercase(string input)
    {
        _orgRepo.Setup(r => r.AddAsync(It.IsAny<OrganizationMember>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _audit.Setup(a => a.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _tupleWriter.Setup(t => t.WriteAsync(It.IsAny<TupleKey>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await CreateSut().AddOrgMemberAsync(Org, User, input, Actor);

        _orgRepo.Verify(r => r.AddAsync(
            It.Is<OrganizationMember>(m => m.Role == input.ToLowerInvariant()),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task AddOrgMemberAsync_Should_Throw_When_RoleIsNull()
    {
        Func<Task> act = () => CreateSut().AddOrgMemberAsync(Org, User, null!, Actor);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task AddOrgMemberAsync_Should_Throw_When_RoleIsInvalid()
    {
        Func<Task> act = () => CreateSut().AddOrgMemberAsync(Org, User, "godking", Actor);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AddOrgMemberAsync_Should_PropagateException_When_TupleWriterThrows()
    {
        // Production code does NOT try/catch tuple writes — it relies on the
        // OpenFgaTupleWriter swallow contract. A throwing mock lets the
        // exception escape.
        _orgRepo.Setup(r => r.AddAsync(It.IsAny<OrganizationMember>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _audit.Setup(a => a.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _tupleWriter.Setup(t => t.WriteAsync(It.IsAny<TupleKey>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("fga down"));

        Func<Task> act = () => CreateSut().AddOrgMemberAsync(Org, User, "admin", Actor);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _orgRepo.Verify(r => r.AddAsync(It.IsAny<OrganizationMember>(), It.IsAny<CancellationToken>()), Times.Once);
        _audit.Verify(a => a.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── RemoveOrgMemberAsync ────────────────────────────────────────

    [Fact]
    public async Task RemoveOrgMemberAsync_Should_ReturnFalse_When_MemberMissing()
    {
        _orgRepo.Setup(r => r.GetAsync(User, Org, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrganizationMember?)null);

        var result = await CreateSut().RemoveOrgMemberAsync(Org, User, Actor);

        result.Should().BeFalse();
        _audit.VerifyNoOtherCalls();
        _tupleWriter.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RemoveOrgMemberAsync_Should_ReturnFalse_When_DeleteAffectsNoRow()
    {
        _orgRepo.Setup(r => r.GetAsync(User, Org, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizationMember { UserId = User, OrganizationId = Org, Role = "admin" });
        _orgRepo.Setup(r => r.RemoveAsync(User, Org, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await CreateSut().RemoveOrgMemberAsync(Org, User, Actor);

        result.Should().BeFalse();
        _audit.VerifyNoOtherCalls();
        _tupleWriter.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RemoveOrgMemberAsync_Should_DeleteAuditAndDeleteTuple()
    {
        _orgRepo.Setup(r => r.GetAsync(User, Org, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizationMember { UserId = User, OrganizationId = Org, Role = "admin" });
        _orgRepo.Setup(r => r.RemoveAsync(User, Org, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        AuditEvent? captured = null;
        _audit.Setup(a => a.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((e, _) => captured = e)
            .Returns(Task.CompletedTask);
        IReadOnlyList<TupleKey>? deletes = null;
        _tupleWriter.Setup(t => t.WriteAsync(
                It.IsAny<IReadOnlyList<TupleKey>>(),
                It.IsAny<IReadOnlyList<TupleKey>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<TupleKey>, IReadOnlyList<TupleKey>, CancellationToken>((_, d, _) => deletes = d)
            .Returns(Task.CompletedTask);

        var result = await CreateSut().RemoveOrgMemberAsync(Org, User, Actor);

        result.Should().BeTrue();
        captured!.EventType.Should().Be("membership_removed");
        deletes.Should().NotBeNull().And.HaveCount(1);
    }

    // ─── Team / Project quick happy-paths ────────────────────────────

    [Fact]
    public async Task AddTeamMemberAsync_Should_WriteRowAuditAndTuple()
    {
        _teamRepo.Setup(r => r.AddAsync(It.IsAny<TeamMember>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        AuditEvent? captured = null;
        _audit.Setup(a => a.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((e, _) => captured = e)
            .Returns(Task.CompletedTask);
        _tupleWriter.Setup(t => t.WriteAsync(It.IsAny<TupleKey>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await CreateSut().AddTeamMemberAsync(Team, User, TeamMemberRole.Manager, Actor);

        captured!.PayloadJson.Should().Contain("\"scope\":\"team\"");
        _tupleWriter.Verify(t => t.WriteAsync(It.IsAny<TupleKey>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveTeamMemberAsync_Should_ReturnFalse_When_MemberMissing()
    {
        _teamRepo.Setup(r => r.GetAsync(User, Team, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TeamMember?)null);

        var result = await CreateSut().RemoveTeamMemberAsync(Team, User, Actor);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task AddProjectMemberAsync_Should_WriteRowAuditAndTuple()
    {
        _projectRepo.Setup(r => r.AddAsync(It.IsAny<ProjectMember>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        AuditEvent? captured = null;
        _audit.Setup(a => a.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((e, _) => captured = e)
            .Returns(Task.CompletedTask);
        _tupleWriter.Setup(t => t.WriteAsync(It.IsAny<TupleKey>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await CreateSut().AddProjectMemberAsync(Project, User, "member", Actor);

        captured!.PayloadJson.Should().Contain("\"scope\":\"project\"");
    }

    [Fact]
    public async Task RemoveProjectMemberAsync_Should_ReturnFalse_When_MemberMissing()
    {
        _projectRepo.Setup(r => r.GetAsync(User, Project, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProjectMember?)null);

        var result = await CreateSut().RemoveProjectMemberAsync(Project, User, Actor);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task AddProjectMemberAsync_Should_Throw_When_RoleIsInvalid()
    {
        Func<Task> act = () => CreateSut().AddProjectMemberAsync(Project, User, "wizard", Actor);
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
