using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.Core.Auditing;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Heimdall.Web.Bootstrap;
using Heimdall.Web.Tests.Bootstrap.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Heimdall.Web.Tests.Bootstrap;

/// <summary>
/// Unit tests for <see cref="OpenFgaBackfillRunner"/>: env-var gating, the
/// "no backfill job registered" warning path, the success path, and the
/// log-and-swallow policy on backfill failure.
/// </summary>
[Collection(EnvironmentVariableSerialCollection.Name)]
public class OpenFgaBackfillRunnerTests
{
    private static EnvironmentVariableScope EnvScope(string? value) =>
        new(new Dictionary<string, string?>
        {
            [OpenFgaBackfillRunner.EnableEnvVar] = value,
        });

    private static OpenFgaBackfillJob BuildJob(
        Mock<IOrganizationRepository>? orgs = null,
        Mock<ITupleWriter>? writer = null,
        Mock<IAuditEventWriter>? audit = null)
    {
        if (orgs is null)
        {
            orgs = new Mock<IOrganizationRepository>();
            orgs
                .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<Organization>());
        }

        var teams = new Mock<ITeamRepository>();
        teams
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Team>());

        var projects = new Mock<IProjectRepository>();
        projects
            .Setup(r => r.GetByTeamAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Project>());

        var tickets = new Mock<ITicketRepository>();
        tickets
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Ticket>());

        var orgMembers = new Mock<IOrganizationMemberRepository>();
        var teamMembers = new Mock<ITeamMemberRepository>();
        var projMembers = new Mock<IProjectMemberRepository>();

        writer ??= new Mock<ITupleWriter>();
        audit ??= new Mock<IAuditEventWriter>();

        return new OpenFgaBackfillJob(
            orgs.Object,
            teams.Object,
            projects.Object,
            tickets.Object,
            orgMembers.Object,
            teamMembers.Object,
            projMembers.Object,
            writer.Object,
            audit.Object,
            NullLogger<OpenFgaBackfillJob>.Instance);
    }

    private static OpenFgaBackfillRunner BuildRunner(OpenFgaBackfillJob? job)
    {
        var services = new ServiceCollection();
        if (job is not null)
        {
            services.AddSingleton(job);
        }

        return new OpenFgaBackfillRunner(
            services.BuildServiceProvider(),
            NullLogger<OpenFgaBackfillRunner>.Instance);
    }

    [Fact]
    public void Constructor_Should_Throw_When_AnyArgumentIsNull()
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        Action act1 = () => new OpenFgaBackfillRunner(null!, NullLogger<OpenFgaBackfillRunner>.Instance);
        Action act2 = () => new OpenFgaBackfillRunner(sp, null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task RunAsync_Should_DoNothing_When_EnvVarUnset()
    {
        using var env = EnvScope(null);
        var orgs = new Mock<IOrganizationRepository>();
        OpenFgaBackfillJob job = BuildJob(orgs);
        OpenFgaBackfillRunner sut = BuildRunner(job);

        await sut.RunAsync(CancellationToken.None);

        orgs.Verify(
            r => r.GetAllAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData(" 1")]
    public async Task RunAsync_Should_DoNothing_When_EnvVarIsNotExactlyOne(string value)
    {
        using var env = EnvScope(value);
        var orgs = new Mock<IOrganizationRepository>();
        OpenFgaBackfillJob job = BuildJob(orgs);
        OpenFgaBackfillRunner sut = BuildRunner(job);

        await sut.RunAsync(CancellationToken.None);

        orgs.Verify(
            r => r.GetAllAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_Should_LogAndReturn_When_EnabledButJobNotRegistered()
    {
        using var env = EnvScope("1");
        OpenFgaBackfillRunner sut = BuildRunner(job: null);

        Func<Task> act = () => sut.RunAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunAsync_Should_InvokeBackfill_When_EnvVarIsExactlyOne()
    {
        using var env = EnvScope("1");
        var orgs = new Mock<IOrganizationRepository>();
        orgs
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Organization>());
        OpenFgaBackfillJob job = BuildJob(orgs);
        OpenFgaBackfillRunner sut = BuildRunner(job);

        await sut.RunAsync(CancellationToken.None);

        orgs.Verify(
            r => r.GetAllAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_Should_SwallowAndContinue_When_BackfillThrows()
    {
        using var env = EnvScope("1");
        var orgs = new Mock<IOrganizationRepository>();
        orgs
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        OpenFgaBackfillJob job = BuildJob(orgs);
        OpenFgaBackfillRunner sut = BuildRunner(job);

        Func<Task> act = () => sut.RunAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunAsync_Should_PropagateCancellation_When_HostShutsDown()
    {
        using var env = EnvScope("1");
        var orgs = new Mock<IOrganizationRepository>();
        orgs
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        OpenFgaBackfillJob job = BuildJob(orgs);
        OpenFgaBackfillRunner sut = BuildRunner(job);

        Func<Task> act = () => sut.RunAsync(CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
