using FluentAssertions;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Heimdall.DAL.Configuration;
using Heimdall.Web.Bootstrap;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;

namespace Heimdall.Web.Tests.Bootstrap;

/// <summary>
/// Unit tests for <see cref="TicketDefaultsBackfiller"/>. Covers the deny-closed
/// skip paths, exception-handling stance, and constructor-guard contract. The
/// happy-path UPDATE behaviour (idempotency, NULL-only updates, row-count logging,
/// assignee_id non-touch) is exercised at the SQL layer indirectly via the
/// matching FluentMigrator NOT NULL flips and is in scope for an end-to-end
/// integration test in a future phase (see <c>docs/implementation/phase-2-checklist.md</c>).
/// </summary>
public class TicketDefaultsBackfillerTests
{
    private const string AdminEmail = "ops@example.com";

    [Fact]
    public void Should_Throw_When_UserManagerIsNull()
    {
        var harness = new TestHarness();
        Action act = () => new TicketDefaultsBackfiller(
            null!,
            harness.OrganizationRepository.Object,
            harness.TeamRepository.Object,
            harness.ProjectRepository.Object,
            harness.Options,
            harness.Logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_OrganizationRepositoryIsNull()
    {
        var harness = new TestHarness();
        Action act = () => new TicketDefaultsBackfiller(
            harness.UserManager.Object,
            null!,
            harness.TeamRepository.Object,
            harness.ProjectRepository.Object,
            harness.Options,
            harness.Logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_TeamRepositoryIsNull()
    {
        var harness = new TestHarness();
        Action act = () => new TicketDefaultsBackfiller(
            harness.UserManager.Object,
            harness.OrganizationRepository.Object,
            null!,
            harness.ProjectRepository.Object,
            harness.Options,
            harness.Logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_ProjectRepositoryIsNull()
    {
        var harness = new TestHarness();
        Action act = () => new TicketDefaultsBackfiller(
            harness.UserManager.Object,
            harness.OrganizationRepository.Object,
            harness.TeamRepository.Object,
            null!,
            harness.Options,
            harness.Logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_OptionsIsNull()
    {
        var harness = new TestHarness();
        Action act = () => new TicketDefaultsBackfiller(
            harness.UserManager.Object,
            harness.OrganizationRepository.Object,
            harness.TeamRepository.Object,
            harness.ProjectRepository.Object,
            null!,
            harness.Logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_LoggerIsNull()
    {
        var harness = new TestHarness();
        Action act = () => new TicketDefaultsBackfiller(
            harness.UserManager.Object,
            harness.OrganizationRepository.Object,
            harness.TeamRepository.Object,
            harness.ProjectRepository.Object,
            harness.Options,
            null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_LogSkipped_When_EmailIsNull()
    {
        var harness = new TestHarness();

        await harness.Backfiller.RunAsync(null, CancellationToken.None);

        harness.Logger.VerifyLog(LogLevel.Information, "skipped");
        harness.UserManager.Verify(m => m.FindByEmailAsync(It.IsAny<string>()), Times.Never);
        harness.OrganizationRepository.Verify(
            r => r.GetBySlugAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Should_LogSkipped_When_EmailIsWhitespace()
    {
        var harness = new TestHarness();

        await harness.Backfiller.RunAsync("   ", CancellationToken.None);

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

        await harness.Backfiller.RunAsync(AdminEmail, CancellationToken.None);

        harness.Logger.VerifyLog(LogLevel.Warning, "bootstrap admin not found");
        harness.OrganizationRepository.Verify(
            r => r.GetBySlugAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Should_LogWarning_When_SeedOrganizationMissing()
    {
        var harness = new TestHarness();
        harness.SetupAdmin(Guid.NewGuid());
        harness.OrganizationRepository
            .Setup(r => r.GetBySlugAsync(DefaultHierarchyBootstrapper.OrganizationSlug, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Organization?)null);

        await harness.Backfiller.RunAsync(AdminEmail, CancellationToken.None);

        harness.Logger.VerifyLog(LogLevel.Warning, "seed organization");
        harness.TeamRepository.Verify(
            r => r.GetBySlugAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Should_LogWarning_When_SeedTeamMissing()
    {
        var harness = new TestHarness();
        var orgId = Guid.NewGuid();
        harness.SetupAdmin(Guid.NewGuid());
        harness.OrganizationRepository
            .Setup(r => r.GetBySlugAsync(DefaultHierarchyBootstrapper.OrganizationSlug, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Organization { Id = orgId, Slug = DefaultHierarchyBootstrapper.OrganizationSlug });
        harness.TeamRepository
            .Setup(r => r.GetBySlugAsync(orgId, DefaultHierarchyBootstrapper.TeamSlug, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Team?)null);

        await harness.Backfiller.RunAsync(AdminEmail, CancellationToken.None);

        harness.Logger.VerifyLog(LogLevel.Warning, "seed team");
        harness.ProjectRepository.Verify(
            r => r.GetBySlugAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Should_LogWarning_When_SeedProjectMissing()
    {
        var harness = new TestHarness();
        var orgId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        harness.SetupAdmin(Guid.NewGuid());
        harness.OrganizationRepository
            .Setup(r => r.GetBySlugAsync(DefaultHierarchyBootstrapper.OrganizationSlug, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Organization { Id = orgId, Slug = DefaultHierarchyBootstrapper.OrganizationSlug });
        harness.TeamRepository
            .Setup(r => r.GetBySlugAsync(orgId, DefaultHierarchyBootstrapper.TeamSlug, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Team { Id = teamId, OrganizationId = orgId, Slug = DefaultHierarchyBootstrapper.TeamSlug });
        harness.ProjectRepository
            .Setup(r => r.GetBySlugAsync(teamId, DefaultHierarchyBootstrapper.ProjectSlug, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Project?)null);

        await harness.Backfiller.RunAsync(AdminEmail, CancellationToken.None);

        harness.Logger.VerifyLog(LogLevel.Warning, "seed project");
    }

    [Fact]
    public async Task Should_NotThrow_When_RepositoryThrowsUnexpectedException()
    {
        // Generic Exception path — logged at Error, swallowed so startup continues.
        var harness = new TestHarness();
        harness.SetupAdmin(Guid.NewGuid());
        harness.OrganizationRepository
            .Setup(r => r.GetBySlugAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transient hiccup"));

        Func<Task> act = () => harness.Backfiller.RunAsync(AdminEmail, CancellationToken.None);

        await act.Should().NotThrowAsync();
        harness.Logger.VerifyLog(LogLevel.Error, "unexpectedly");
    }

    [Fact]
    public async Task Should_NotThrow_When_RepositoryThrowsNpgsqlException()
    {
        // NpgsqlException is the dedicated transient-DB-failure path; it logs a
        // Warning rather than an Error so a single boot-time hiccup does not
        // pollute the error stream.
        var harness = new TestHarness();
        harness.SetupAdmin(Guid.NewGuid());
        harness.OrganizationRepository
            .Setup(r => r.GetBySlugAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NpgsqlException("simulated transient db error"));

        Func<Task> act = () => harness.Backfiller.RunAsync(AdminEmail, CancellationToken.None);

        await act.Should().NotThrowAsync();
        harness.Logger.VerifyLog(LogLevel.Warning, "Postgres error");
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

        Func<Task> act = () => harness.Backfiller.RunAsync(AdminEmail, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ------------------------------------------------------------------
    // Test harness.
    // ------------------------------------------------------------------
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
            Options = Microsoft.Extensions.Options.Options.Create(new DataOptions
            {
                // Connection string is only opened when BackfillAsync runs to completion;
                // every Suite-B path stops short of that. Value is therefore a placeholder
                // that should never be dialled — if it is, the test is exercising a path
                // that belongs in the integration suite.
                PostgresConnectionString = "Host=unused;Database=unused;Username=unused;Password=unused",
            });
            Logger = new Mock<ILogger<TicketDefaultsBackfiller>>();
            Logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            Backfiller = new TicketDefaultsBackfiller(
                UserManager.Object,
                OrganizationRepository.Object,
                TeamRepository.Object,
                ProjectRepository.Object,
                Options,
                Logger.Object);
        }

        public Mock<IUserStore<HeimdallUser>> UserStore { get; }

        public Mock<UserManager<HeimdallUser>> UserManager { get; }

        public Mock<IOrganizationRepository> OrganizationRepository { get; }

        public Mock<ITeamRepository> TeamRepository { get; }

        public Mock<IProjectRepository> ProjectRepository { get; }

        public IOptions<DataOptions> Options { get; }

        public Mock<ILogger<TicketDefaultsBackfiller>> Logger { get; }

        public TicketDefaultsBackfiller Backfiller { get; }

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
    }
}
