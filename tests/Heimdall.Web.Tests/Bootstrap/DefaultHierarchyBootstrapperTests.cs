using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Heimdall.Web.Bootstrap;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Moq;

namespace Heimdall.Web.Tests.Bootstrap;

public class DefaultHierarchyBootstrapperTests
{
    private const string AdminEmail = "ops@example.com";

    [Fact]
    public void Should_Throw_When_UserManagerIsNull()
    {
        var harness = new TestHarness();
        Action act = () => new DefaultHierarchyBootstrapper(
            null!,
            harness.OrganizationRepository.Object,
            harness.TeamRepository.Object,
            harness.ProjectRepository.Object,
            harness.OrganizationMemberRepository.Object,
            harness.TeamMemberRepository.Object,
            harness.ProjectMemberRepository.Object,
            harness.TupleWriter.Object,
            harness.Logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_OrganizationRepositoryIsNull()
    {
        var harness = new TestHarness();
        Action act = () => new DefaultHierarchyBootstrapper(
            harness.UserManager.Object,
            null!,
            harness.TeamRepository.Object,
            harness.ProjectRepository.Object,
            harness.OrganizationMemberRepository.Object,
            harness.TeamMemberRepository.Object,
            harness.ProjectMemberRepository.Object,
            harness.TupleWriter.Object,
            harness.Logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_TeamRepositoryIsNull()
    {
        var harness = new TestHarness();
        Action act = () => new DefaultHierarchyBootstrapper(
            harness.UserManager.Object,
            harness.OrganizationRepository.Object,
            null!,
            harness.ProjectRepository.Object,
            harness.OrganizationMemberRepository.Object,
            harness.TeamMemberRepository.Object,
            harness.ProjectMemberRepository.Object,
            harness.TupleWriter.Object,
            harness.Logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_ProjectRepositoryIsNull()
    {
        var harness = new TestHarness();
        Action act = () => new DefaultHierarchyBootstrapper(
            harness.UserManager.Object,
            harness.OrganizationRepository.Object,
            harness.TeamRepository.Object,
            null!,
            harness.OrganizationMemberRepository.Object,
            harness.TeamMemberRepository.Object,
            harness.ProjectMemberRepository.Object,
            harness.TupleWriter.Object,
            harness.Logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_OrganizationMemberRepositoryIsNull()
    {
        var harness = new TestHarness();
        Action act = () => new DefaultHierarchyBootstrapper(
            harness.UserManager.Object,
            harness.OrganizationRepository.Object,
            harness.TeamRepository.Object,
            harness.ProjectRepository.Object,
            null!,
            harness.TeamMemberRepository.Object,
            harness.ProjectMemberRepository.Object,
            harness.TupleWriter.Object,
            harness.Logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_TeamMemberRepositoryIsNull()
    {
        var harness = new TestHarness();
        Action act = () => new DefaultHierarchyBootstrapper(
            harness.UserManager.Object,
            harness.OrganizationRepository.Object,
            harness.TeamRepository.Object,
            harness.ProjectRepository.Object,
            harness.OrganizationMemberRepository.Object,
            null!,
            harness.ProjectMemberRepository.Object,
            harness.TupleWriter.Object,
            harness.Logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_ProjectMemberRepositoryIsNull()
    {
        var harness = new TestHarness();
        Action act = () => new DefaultHierarchyBootstrapper(
            harness.UserManager.Object,
            harness.OrganizationRepository.Object,
            harness.TeamRepository.Object,
            harness.ProjectRepository.Object,
            harness.OrganizationMemberRepository.Object,
            harness.TeamMemberRepository.Object,
            null!,
            harness.TupleWriter.Object,
            harness.Logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_LoggerIsNull()
    {
        var harness = new TestHarness();
        Action act = () => new DefaultHierarchyBootstrapper(
            harness.UserManager.Object,
            harness.OrganizationRepository.Object,
            harness.TeamRepository.Object,
            harness.ProjectRepository.Object,
            harness.OrganizationMemberRepository.Object,
            harness.TeamMemberRepository.Object,
            harness.ProjectMemberRepository.Object,
            harness.TupleWriter.Object,
            null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_LogSkipped_When_EmailIsNull()
    {
        var harness = new TestHarness();

        await harness.Bootstrapper.RunAsync(null, CancellationToken.None);

        harness.Logger.VerifyLog(LogLevel.Information, "skipped");
        harness.UserManager.Verify(m => m.FindByEmailAsync(It.IsAny<string>()), Times.Never);
        harness.OrganizationRepository.Verify(
            r => r.GetBySlugAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Should_LogSkipped_When_EmailIsWhitespace()
    {
        var harness = new TestHarness();

        await harness.Bootstrapper.RunAsync("   ", CancellationToken.None);

        harness.Logger.VerifyLog(LogLevel.Information, "skipped");
        harness.UserManager.Verify(m => m.FindByEmailAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Should_LogWarning_When_BootstrapAdminNotFound()
    {
        var harness = new TestHarness();
        harness.UserManager
            .Setup(m => m.FindByEmailAsync(AdminEmail))
            .ReturnsAsync((HeimdallUser?)null);

        await harness.Bootstrapper.RunAsync(AdminEmail, CancellationToken.None);

        harness.Logger.VerifyLog(LogLevel.Warning, "not found");
        harness.OrganizationRepository.Verify(
            r => r.GetBySlugAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.OrganizationRepository.Verify(
            r => r.CreateAsync(It.IsAny<Organization>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Should_CreateAllRows_When_FreshDatabase()
    {
        var harness = new TestHarness();
        var adminId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        harness.SetupAdmin(adminId);
        harness.SetupFreshHierarchy(orgId, teamId, projectId);

        await harness.Bootstrapper.RunAsync(AdminEmail, CancellationToken.None);

        // Hierarchy creates — assert each parent inserted with created_by = admin.Id.
        harness.OrganizationRepository.Verify(
            r => r.CreateAsync(
                It.Is<Organization>(o =>
                    o.Slug == DefaultHierarchyBootstrapper.OrganizationSlug
                    && o.CreatedBy == adminId),
                It.IsAny<CancellationToken>()),
            Times.Once);
        harness.TeamRepository.Verify(
            r => r.CreateAsync(
                It.Is<Team>(t =>
                    t.Slug == DefaultHierarchyBootstrapper.TeamSlug
                    && t.OrganizationId == orgId
                    && t.CreatedBy == adminId),
                It.IsAny<CancellationToken>()),
            Times.Once);
        harness.ProjectRepository.Verify(
            r => r.CreateAsync(
                It.Is<Project>(p =>
                    p.Slug == DefaultHierarchyBootstrapper.ProjectSlug
                    && p.TeamId == teamId
                    && p.CreatedBy == adminId),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Membership creates — owner / manager / owner against the freshly-created parents.
        harness.OrganizationMemberRepository.Verify(
            r => r.AddAsync(
                It.Is<OrganizationMember>(m =>
                    m.UserId == adminId
                    && m.OrganizationId == orgId
                    && m.Role == DefaultHierarchyBootstrapper.OwnerRole
                    && m.AddedBy == adminId),
                It.IsAny<CancellationToken>()),
            Times.Once);
        harness.TeamMemberRepository.Verify(
            r => r.AddAsync(
                It.Is<TeamMember>(m =>
                    m.UserId == adminId
                    && m.TeamId == teamId
                    && m.Role == TeamMemberRole.Manager
                    && m.AddedBy == adminId),
                It.IsAny<CancellationToken>()),
            Times.Once);
        harness.ProjectMemberRepository.Verify(
            r => r.AddAsync(
                It.Is<ProjectMember>(m =>
                    m.UserId == adminId
                    && m.ProjectId == projectId
                    && m.Role == DefaultHierarchyBootstrapper.OwnerRole
                    && m.AddedBy == adminId),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_BeNoOp_When_FullySeeded()
    {
        var harness = new TestHarness();
        var adminId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        harness.SetupAdmin(adminId);
        harness.SetupFullySeededHierarchy(adminId, orgId, teamId, projectId);

        await harness.Bootstrapper.RunAsync(AdminEmail, CancellationToken.None);

        harness.OrganizationRepository.Verify(
            r => r.CreateAsync(It.IsAny<Organization>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.TeamRepository.Verify(
            r => r.CreateAsync(It.IsAny<Team>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.ProjectRepository.Verify(
            r => r.CreateAsync(It.IsAny<Project>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.OrganizationMemberRepository.Verify(
            r => r.AddAsync(It.IsAny<OrganizationMember>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.TeamMemberRepository.Verify(
            r => r.AddAsync(It.IsAny<TeamMember>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.ProjectMemberRepository.Verify(
            r => r.AddAsync(It.IsAny<ProjectMember>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Should_EmitFiveSeedTuples_When_FreshDatabase()
    {
        var harness = new TestHarness();
        var adminId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        harness.SetupAdmin(adminId);
        harness.SetupFreshHierarchy(orgId, teamId, projectId);

        IReadOnlyList<TupleKey>? capturedWrites = null;
        IReadOnlyList<TupleKey>? capturedDeletes = null;
        harness.TupleWriter
            .Setup(w => w.WriteAsync(
                It.IsAny<IReadOnlyList<TupleKey>>(),
                It.IsAny<IReadOnlyList<TupleKey>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<TupleKey>, IReadOnlyList<TupleKey>, CancellationToken>(
                (writes, deletes, _) =>
                {
                    capturedWrites = writes;
                    capturedDeletes = deletes;
                })
            .Returns(Task.CompletedTask);

        await harness.Bootstrapper.RunAsync(AdminEmail, CancellationToken.None);

        harness.TupleWriter.Verify(
            w => w.WriteAsync(
                It.IsAny<IReadOnlyList<TupleKey>>(),
                It.IsAny<IReadOnlyList<TupleKey>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        capturedWrites.Should().NotBeNull();
        capturedWrites!.Should().HaveCount(5);
        capturedDeletes.Should().BeEmpty();

        capturedWrites!.Should().BeEquivalentTo(new[]
        {
            TupleShapes.TeamParentOrg(teamId, orgId),
            TupleShapes.ProjectParentTeam(projectId, teamId),
            TupleShapes.OrgMemberFromRole(orgId, adminId, DefaultHierarchyBootstrapper.OwnerRole),
            TupleShapes.TeamAdminFromRole(teamId, adminId, TeamMemberRole.Manager),
            TupleShapes.ProjectMemberFromRole(projectId, adminId, DefaultHierarchyBootstrapper.OwnerRole),
        });
    }

    [Fact]
    public async Task Should_StillEmitSeedTuples_When_FullySeeded()
    {
        // The seed-tuple write is unconditional — it must run on every boot so a
        // missing OpenFGA tuple after a manual SQL fix-up gets re-emitted.
        var harness = new TestHarness();
        var adminId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        harness.SetupAdmin(adminId);
        harness.SetupFullySeededHierarchy(adminId, orgId, teamId, projectId);

        await harness.Bootstrapper.RunAsync(AdminEmail, CancellationToken.None);

        harness.TupleWriter.Verify(
            w => w.WriteAsync(
                It.Is<IReadOnlyList<TupleKey>>(c => c.Count == 5),
                It.Is<IReadOnlyList<TupleKey>>(c => c.Count == 0),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_OnlyFillGaps_When_PartiallySeeded()
    {
        // Org exists, team is missing → bootstrap creates team + project + all 3 memberships,
        // but does NOT create another org. Exercises the GetBySlugAsync-then-CreateAsync seam.
        var harness = new TestHarness();
        var adminId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        harness.SetupAdmin(adminId);

        // Existing org row.
        harness.OrganizationRepository
            .Setup(r => r.GetBySlugAsync(
                DefaultHierarchyBootstrapper.OrganizationSlug,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Organization
            {
                Id = orgId,
                Slug = DefaultHierarchyBootstrapper.OrganizationSlug,
                Name = "Heimdall",
                CreatedBy = adminId,
            });

        // Team and project both missing.
        harness.TeamRepository
            .Setup(r => r.GetBySlugAsync(orgId, DefaultHierarchyBootstrapper.TeamSlug, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Team?)null);
        harness.TeamRepository
            .Setup(r => r.CreateAsync(It.IsAny<Team>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(teamId);
        harness.ProjectRepository
            .Setup(r => r.GetBySlugAsync(teamId, DefaultHierarchyBootstrapper.ProjectSlug, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Project?)null);
        harness.ProjectRepository
            .Setup(r => r.CreateAsync(It.IsAny<Project>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(projectId);

        // All memberships missing.
        harness.OrganizationMemberRepository
            .Setup(r => r.GetAsync(adminId, orgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrganizationMember?)null);
        harness.TeamMemberRepository
            .Setup(r => r.GetAsync(adminId, teamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TeamMember?)null);
        harness.ProjectMemberRepository
            .Setup(r => r.GetAsync(adminId, projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProjectMember?)null);

        await harness.Bootstrapper.RunAsync(AdminEmail, CancellationToken.None);

        harness.OrganizationRepository.Verify(
            r => r.CreateAsync(It.IsAny<Organization>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.TeamRepository.Verify(
            r => r.CreateAsync(It.IsAny<Team>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.ProjectRepository.Verify(
            r => r.CreateAsync(It.IsAny<Project>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.OrganizationMemberRepository.Verify(
            r => r.AddAsync(It.IsAny<OrganizationMember>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.TeamMemberRepository.Verify(
            r => r.AddAsync(It.IsAny<TeamMember>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.ProjectMemberRepository.Verify(
            r => r.AddAsync(It.IsAny<ProjectMember>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Should_OnlyAddMissingMemberships_When_HierarchyExistsButMembershipsMissing()
    {
        // Hierarchy exists (e.g. seeded by SQL fixture), but membership rows are missing.
        // This is the post-cutover scenario the OpenFGA tuple backfill protects against —
        // bootstrapper must add the rows so OpenFGA produces admin tuples for the seed hierarchy.
        var harness = new TestHarness();
        var adminId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        harness.SetupAdmin(adminId);

        harness.OrganizationRepository
            .Setup(r => r.GetBySlugAsync(DefaultHierarchyBootstrapper.OrganizationSlug, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Organization { Id = orgId, Slug = DefaultHierarchyBootstrapper.OrganizationSlug, CreatedBy = adminId });
        harness.TeamRepository
            .Setup(r => r.GetBySlugAsync(orgId, DefaultHierarchyBootstrapper.TeamSlug, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Team { Id = teamId, OrganizationId = orgId, Slug = DefaultHierarchyBootstrapper.TeamSlug, CreatedBy = adminId });
        harness.ProjectRepository
            .Setup(r => r.GetBySlugAsync(teamId, DefaultHierarchyBootstrapper.ProjectSlug, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = projectId, TeamId = teamId, Slug = DefaultHierarchyBootstrapper.ProjectSlug, CreatedBy = adminId });

        harness.OrganizationMemberRepository
            .Setup(r => r.GetAsync(adminId, orgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrganizationMember?)null);
        harness.TeamMemberRepository
            .Setup(r => r.GetAsync(adminId, teamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TeamMember?)null);
        harness.ProjectMemberRepository
            .Setup(r => r.GetAsync(adminId, projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProjectMember?)null);

        await harness.Bootstrapper.RunAsync(AdminEmail, CancellationToken.None);

        harness.OrganizationRepository.Verify(
            r => r.CreateAsync(It.IsAny<Organization>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.TeamRepository.Verify(
            r => r.CreateAsync(It.IsAny<Team>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.ProjectRepository.Verify(
            r => r.CreateAsync(It.IsAny<Project>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.OrganizationMemberRepository.Verify(
            r => r.AddAsync(It.IsAny<OrganizationMember>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.TeamMemberRepository.Verify(
            r => r.AddAsync(It.IsAny<TeamMember>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.ProjectMemberRepository.Verify(
            r => r.AddAsync(It.IsAny<ProjectMember>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Should_NotThrow_When_RepositoryThrowsUnexpectedException()
    {
        var harness = new TestHarness();
        harness.SetupAdmin(Guid.NewGuid());
        harness.OrganizationRepository
            .Setup(r => r.GetBySlugAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transient DB hiccup"));

        Func<Task> act = () => harness.Bootstrapper.RunAsync(AdminEmail, CancellationToken.None);

        await act.Should().NotThrowAsync();
        harness.Logger.VerifyLog(LogLevel.Error, "bootstrap failed unexpectedly");
    }

    [Fact]
    public async Task Should_PropagateOperationCanceledException_When_TokenCancelled()
    {
        var harness = new TestHarness();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        harness.UserManager
            .Setup(m => m.FindByEmailAsync(It.IsAny<string>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        Func<Task> act = () => harness.Bootstrapper.RunAsync(AdminEmail, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---------------------------------------------------------------------
    // Test harness
    // ---------------------------------------------------------------------
    private sealed class TestHarness
    {
        public TestHarness()
        {
            UserStore = new Mock<IUserStore<HeimdallUser>>();
            UserManager = new Mock<UserManager<HeimdallUser>>(
                UserStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);
            OrganizationRepository = new Mock<IOrganizationRepository>();
            TeamRepository = new Mock<ITeamRepository>();
            ProjectRepository = new Mock<IProjectRepository>();
            OrganizationMemberRepository = new Mock<IOrganizationMemberRepository>();
            TeamMemberRepository = new Mock<ITeamMemberRepository>();
            ProjectMemberRepository = new Mock<IProjectMemberRepository>();
            TupleWriter = new Mock<ITupleWriter>();
            Logger = new Mock<ILogger<DefaultHierarchyBootstrapper>>();
            Logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            Bootstrapper = new DefaultHierarchyBootstrapper(
                UserManager.Object,
                OrganizationRepository.Object,
                TeamRepository.Object,
                ProjectRepository.Object,
                OrganizationMemberRepository.Object,
                TeamMemberRepository.Object,
                ProjectMemberRepository.Object,
                TupleWriter.Object,
                Logger.Object);
        }

        public Mock<IUserStore<HeimdallUser>> UserStore { get; }

        public Mock<UserManager<HeimdallUser>> UserManager { get; }

        public Mock<IOrganizationRepository> OrganizationRepository { get; }

        public Mock<ITeamRepository> TeamRepository { get; }

        public Mock<IProjectRepository> ProjectRepository { get; }

        public Mock<IOrganizationMemberRepository> OrganizationMemberRepository { get; }

        public Mock<ITeamMemberRepository> TeamMemberRepository { get; }

        public Mock<IProjectMemberRepository> ProjectMemberRepository { get; }

        public Mock<ITupleWriter> TupleWriter { get; }

        public Mock<ILogger<DefaultHierarchyBootstrapper>> Logger { get; }

        public DefaultHierarchyBootstrapper Bootstrapper { get; }

        public void SetupAdmin(Guid adminId)
        {
            UserManager
                .Setup(m => m.FindByEmailAsync(AdminEmail))
                .ReturnsAsync(new HeimdallUser
                {
                    Id = adminId,
                    Email = AdminEmail,
                    NormalizedEmail = AdminEmail.ToUpperInvariant(),
                    SystemAdmin = true,
                });
        }

        public void SetupFreshHierarchy(Guid orgId, Guid teamId, Guid projectId)
        {
            OrganizationRepository
                .Setup(r => r.GetBySlugAsync(DefaultHierarchyBootstrapper.OrganizationSlug, It.IsAny<CancellationToken>()))
                .ReturnsAsync((Organization?)null);
            OrganizationRepository
                .Setup(r => r.CreateAsync(It.IsAny<Organization>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(orgId);

            TeamRepository
                .Setup(r => r.GetBySlugAsync(orgId, DefaultHierarchyBootstrapper.TeamSlug, It.IsAny<CancellationToken>()))
                .ReturnsAsync((Team?)null);
            TeamRepository
                .Setup(r => r.CreateAsync(It.IsAny<Team>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(teamId);

            ProjectRepository
                .Setup(r => r.GetBySlugAsync(teamId, DefaultHierarchyBootstrapper.ProjectSlug, It.IsAny<CancellationToken>()))
                .ReturnsAsync((Project?)null);
            ProjectRepository
                .Setup(r => r.CreateAsync(It.IsAny<Project>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(projectId);

            OrganizationMemberRepository
                .Setup(r => r.GetAsync(It.IsAny<Guid>(), orgId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((OrganizationMember?)null);
            TeamMemberRepository
                .Setup(r => r.GetAsync(It.IsAny<Guid>(), teamId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((TeamMember?)null);
            ProjectMemberRepository
                .Setup(r => r.GetAsync(It.IsAny<Guid>(), projectId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((ProjectMember?)null);
        }

        public void SetupFullySeededHierarchy(Guid adminId, Guid orgId, Guid teamId, Guid projectId)
        {
            OrganizationRepository
                .Setup(r => r.GetBySlugAsync(DefaultHierarchyBootstrapper.OrganizationSlug, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Organization { Id = orgId, Slug = DefaultHierarchyBootstrapper.OrganizationSlug, CreatedBy = adminId });
            TeamRepository
                .Setup(r => r.GetBySlugAsync(orgId, DefaultHierarchyBootstrapper.TeamSlug, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Team { Id = teamId, OrganizationId = orgId, Slug = DefaultHierarchyBootstrapper.TeamSlug, CreatedBy = adminId });
            ProjectRepository
                .Setup(r => r.GetBySlugAsync(teamId, DefaultHierarchyBootstrapper.ProjectSlug, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Project { Id = projectId, TeamId = teamId, Slug = DefaultHierarchyBootstrapper.ProjectSlug, CreatedBy = adminId });

            OrganizationMemberRepository
                .Setup(r => r.GetAsync(adminId, orgId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OrganizationMember { UserId = adminId, OrganizationId = orgId, Role = "owner", AddedBy = adminId });
            TeamMemberRepository
                .Setup(r => r.GetAsync(adminId, teamId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TeamMember { UserId = adminId, TeamId = teamId, Role = TeamMemberRole.Manager, AddedBy = adminId });
            ProjectMemberRepository
                .Setup(r => r.GetAsync(adminId, projectId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProjectMember { UserId = adminId, ProjectId = projectId, Role = "owner", AddedBy = adminId });
        }
    }
}
