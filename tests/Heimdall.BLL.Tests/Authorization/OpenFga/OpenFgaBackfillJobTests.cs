using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.Core.Auditing;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Heimdall.BLL.Tests.Authorization.OpenFga;

/// <summary>
/// Unit tests for <see cref="OpenFgaBackfillJob"/>. The seven repository
/// interfaces and both writer interfaces are mocked; the production code is
/// driven through deterministic in-memory shapes so tuple counts can be
/// asserted exactly.
/// </summary>
public class OpenFgaBackfillJobTests
{
    private sealed class Harness
    {
        public Mock<IOrganizationRepository> Organizations { get; } = new();

        public Mock<ITeamRepository> Teams { get; } = new();

        public Mock<IProjectRepository> Projects { get; } = new();

        public Mock<ITicketRepository> Tickets { get; } = new();

        public Mock<IOrganizationMemberRepository> OrgMembers { get; } = new();

        public Mock<ITeamMemberRepository> TeamMembers { get; } = new();

        public Mock<IProjectMemberRepository> ProjectMembers { get; } = new();

        public Mock<ITupleWriter> TupleWriter { get; } = new();

        public Mock<IAuditEventWriter> AuditWriter { get; } = new();

        public List<TupleKey> AllWrittenTuples { get; } = new();

        public List<int> WriteBatchSizes { get; } = new();

        public Harness()
        {
            // Wire empty defaults so every call returns an empty list.
            Organizations
                .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<Organization>());
            Teams
                .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<Team>());
            Projects
                .Setup(r => r.GetByTeamAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<Project>());
            Tickets
                .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<Ticket>());
            OrgMembers
                .Setup(r => r.GetByParentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<OrganizationMember>());
            TeamMembers
                .Setup(r => r.GetByParentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<TeamMember>());
            ProjectMembers
                .Setup(r => r.GetByParentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<ProjectMember>());

            TupleWriter
                .Setup(w => w.WriteAsync(
                    It.IsAny<IReadOnlyList<TupleKey>>(),
                    It.IsAny<IReadOnlyList<TupleKey>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<IReadOnlyList<TupleKey>, IReadOnlyList<TupleKey>, CancellationToken>(
                    (writes, _, _) =>
                    {
                        AllWrittenTuples.AddRange(writes);
                        WriteBatchSizes.Add(writes.Count);
                    })
                .Returns(Task.CompletedTask);
        }

        public OpenFgaBackfillJob BuildJob() => new(
            Organizations.Object,
            Teams.Object,
            Projects.Object,
            Tickets.Object,
            OrgMembers.Object,
            TeamMembers.Object,
            ProjectMembers.Object,
            TupleWriter.Object,
            AuditWriter.Object,
            NullLogger<OpenFgaBackfillJob>.Instance);
    }

    // ─── Empty DB ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_Should_NotInvokeWriter_When_DatabaseIsEmpty()
    {
        var harness = new Harness();
        var job = harness.BuildJob();

        var result = await job.RunAsync(CancellationToken.None);

        harness.TupleWriter.Verify(
            w => w.WriteAsync(
                It.IsAny<IReadOnlyList<TupleKey>>(),
                It.IsAny<IReadOnlyList<TupleKey>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        result.TuplesWritten.Should().Be(0);
        result.OrganizationsScanned.Should().Be(0);
        result.TeamsScanned.Should().Be(0);
        result.ProjectsScanned.Should().Be(0);
        result.MembershipsScanned.Should().Be(0);
        result.TicketsScanned.Should().Be(0);
        harness.AuditWriter.Verify(
            a => a.WriteAsync(
                It.Is<AuditEvent>(e =>
                    e.EventType == "openfga_backfill_completed"
                    && e.PayloadJson.Contains("\"organizations_scanned\":0", StringComparison.Ordinal)
                    && e.PayloadJson.Contains("\"teams_scanned\":0", StringComparison.Ordinal)
                    && e.PayloadJson.Contains("\"tuples_written\":0", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ─── Mixed DB ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_Should_EmitCorrectTupleSet_When_DatabaseHasMixedRows()
    {
        var harness = new Harness();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var teamC = Guid.NewGuid();
        var projA = Guid.NewGuid();
        var projB = Guid.NewGuid();
        var projC = Guid.NewGuid();
        var projD = Guid.NewGuid();
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        // 2 orgs, 3 teams (2 in orgA, 1 in orgB), 4 projects (2 under teamA, 1 under teamB, 1 under teamC).
        harness.Organizations
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new Organization { Id = orgA, Slug = "a" },
                new Organization { Id = orgB, Slug = "b" },
            });
        harness.Teams
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new Team { Id = teamA, OrganizationId = orgA },
                new Team { Id = teamB, OrganizationId = orgA },
                new Team { Id = teamC, OrganizationId = orgB },
            });
        harness.Projects
            .Setup(r => r.GetByTeamAsync(teamA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new Project { Id = projA, TeamId = teamA },
                new Project { Id = projB, TeamId = teamA },
            });
        harness.Projects
            .Setup(r => r.GetByTeamAsync(teamB, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Project { Id = projC, TeamId = teamB } });
        harness.Projects
            .Setup(r => r.GetByTeamAsync(teamC, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Project { Id = projD, TeamId = teamC } });

        // 5 memberships: 2 org (admin, member roles), 2 team (Manager, Viewer), 1 project (owner).
        harness.OrgMembers
            .Setup(r => r.GetByParentAsync(orgA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new OrganizationMember { UserId = user1, OrganizationId = orgA, Role = "admin" },
                new OrganizationMember { UserId = user2, OrganizationId = orgA, Role = "member" },
            });
        harness.TeamMembers
            .Setup(r => r.GetByParentAsync(teamA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new TeamMember { UserId = user1, TeamId = teamA, Role = TeamMemberRole.Manager },
                new TeamMember { UserId = user2, TeamId = teamA, Role = TeamMemberRole.Viewer },
            });
        harness.ProjectMembers
            .Setup(r => r.GetByParentAsync(projA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ProjectMember { UserId = user1, ProjectId = projA, Role = "owner" },
            });

        // 6 tickets: 2 with assignees, 4 without.
        harness.Tickets
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new Ticket { Id = 1, ProjectId = projA, ReporterId = user1, AssigneeId = user2 },
                new Ticket { Id = 2, ProjectId = projA, ReporterId = user1, AssigneeId = null },
                new Ticket { Id = 3, ProjectId = projB, ReporterId = user2, AssigneeId = null },
                new Ticket { Id = 4, ProjectId = projC, ReporterId = user1, AssigneeId = user2 },
                new Ticket { Id = 5, ProjectId = projC, ReporterId = user2, AssigneeId = null },
                new Ticket { Id = 6, ProjectId = projD, ReporterId = user1, AssigneeId = null },
            });

        var job = harness.BuildJob();

        var result = await job.RunAsync(CancellationToken.None);

        result.OrganizationsScanned.Should().Be(2);
        result.TeamsScanned.Should().Be(3);
        result.ProjectsScanned.Should().Be(4);
        result.MembershipsScanned.Should().Be(5);
        result.TicketsScanned.Should().Be(6);

        // Tuple math:
        //   2 org memberships (1 admin, 1 member)
        //   3 team#parent_org (one per team)
        //   2 team memberships (1 admin, 1 viewer)
        //   4 project#parent_team (one per project)
        //   1 project membership
        //   6 ticket#parent_project + 6 ticket#reporter + 2 ticket#assignee = 14
        // Total = 2 + 3 + 2 + 4 + 1 + 14 = 26.
        harness.AllWrittenTuples.Should().HaveCount(26);
        result.TuplesWritten.Should().Be(26);

        harness.AllWrittenTuples
            .Count(t => t.Relation == TupleShapes.ParentOrgRelation)
            .Should().Be(3);
        harness.AllWrittenTuples
            .Count(t => t.Relation == TupleShapes.ParentTeamRelation)
            .Should().Be(4);
        harness.AllWrittenTuples
            .Count(t => t.Relation == TupleShapes.ParentProjectRelation)
            .Should().Be(6);
        harness.AllWrittenTuples
            .Count(t => t.Relation == TupleShapes.ReporterRelation)
            .Should().Be(6);
        harness.AllWrittenTuples
            .Count(t => t.Relation == TupleShapes.AssigneeRelation)
            .Should().Be(2);
        harness.AllWrittenTuples
            .Count(t => t.Relation == TupleShapes.AdminRelation)
            .Should().Be(3); // org admin (1) + project admin (1) + team admin (1)
        harness.AllWrittenTuples
            .Count(t => t.Relation == TupleShapes.MemberRelation)
            .Should().Be(1); // org member
        harness.AllWrittenTuples
            .Count(t => t.Relation == TupleShapes.ViewerRelation)
            .Should().Be(1); // team viewer
    }

    // ─── Chunking ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_Should_ChunkWritesAtHundred_When_BufferOverflows()
    {
        var harness = new Harness();
        var orgId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        harness.Organizations
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Organization { Id = orgId } });
        harness.Teams
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Team { Id = teamId, OrganizationId = orgId } });
        harness.Projects
            .Setup(r => r.GetByTeamAsync(teamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Project { Id = projectId, TeamId = teamId } });

        // 100 tickets without assignees ⇒ 100 × 2 = 200 tuples just from tickets,
        // plus the team/project parent and members. Bumping to 130 tickets gives
        // 260 + 2 = 262 tuples — comfortably above 250 and forcing ≥ 3 flushes.
        var ticketRows = new List<Ticket>();
        for (int i = 1; i <= 130; i++)
        {
            ticketRows.Add(new Ticket
            {
                Id = i,
                ProjectId = projectId,
                ReporterId = Guid.NewGuid(),
                AssigneeId = null,
            });
        }

        harness.Tickets
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ticketRows);

        var job = harness.BuildJob();

        await job.RunAsync(CancellationToken.None);

        // 1 team#parent_org + 1 project#parent_team + 130 × (parent_project + reporter) = 262.
        harness.AllWrittenTuples.Should().HaveCount(262);
        harness.WriteBatchSizes.Should().HaveCountGreaterThanOrEqualTo(3);
        harness.WriteBatchSizes.Should().OnlyContain(c => c > 0 && c <= 100);
    }

    // ─── Re-run idempotency ───────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_Should_CompleteAndAuditOnEveryRun_When_InvokedRepeatedly()
    {
        var harness = new Harness();
        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        harness.Organizations
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Organization { Id = orgId } });
        harness.OrgMembers
            .Setup(r => r.GetByParentAsync(orgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new OrganizationMember { UserId = userId, OrganizationId = orgId, Role = "owner" },
            });

        var job = harness.BuildJob();

        Func<Task> firstRun = () => job.RunAsync(CancellationToken.None);
        Func<Task> secondRun = () => job.RunAsync(CancellationToken.None);

        await firstRun.Should().NotThrowAsync();
        await secondRun.Should().NotThrowAsync();

        harness.AuditWriter.Verify(
            a => a.WriteAsync(
                It.Is<AuditEvent>(e => e.EventType == "openfga_backfill_completed"),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // ─── Cancellation ─────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_Should_PropagateOperationCanceled_When_RepositoryThrowsOce()
    {
        var harness = new Harness();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        harness.Organizations
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var job = harness.BuildJob();

        Func<Task> act = () => job.RunAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
