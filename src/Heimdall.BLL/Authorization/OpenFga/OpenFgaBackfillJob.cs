using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Auditing;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Microsoft.Extensions.Logging;

namespace Heimdall.BLL.Authorization.OpenFga;

/// <summary>
/// One-shot, idempotent backfill that walks every relational hierarchy row and
/// writes the matching tuple set into OpenFGA — Phase 3.4 step 8.
/// </summary>
/// <remarks>
/// <para>
/// The job is gated externally by the <c>HEIMDALL_OPENFGA_BACKFILL=1</c> env var
/// (see <c>OpenFgaBackfillRunner</c>). It is safe to re-run: writes go through
/// <see cref="ITupleWriter"/>, which logs and audits any per-chunk failures
/// (including the SDK's "tuple already exists" error, which is the expected
/// outcome when re-running after a successful first pass).
/// </para>
/// </remarks>
public sealed class OpenFgaBackfillJob
{
    private const int MaxTuplesPerWrite = 100;

    private static readonly JsonSerializerOptions AuditPayloadJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IOrganizationRepository _organizationRepository;
    private readonly ITeamRepository _teamRepository;
    private readonly IProjectRepository _projectRepository;
    private readonly ITicketRepository _ticketRepository;
    private readonly IOrganizationMemberRepository _organizationMemberRepository;
    private readonly ITeamMemberRepository _teamMemberRepository;
    private readonly IProjectMemberRepository _projectMemberRepository;
    private readonly ITupleWriter _tupleWriter;
    private readonly IAuditEventWriter _auditWriter;
    private readonly ILogger<OpenFgaBackfillJob> _logger;

    /// <summary>Initializes a new instance.</summary>
    public OpenFgaBackfillJob(
        IOrganizationRepository organizationRepository,
        ITeamRepository teamRepository,
        IProjectRepository projectRepository,
        ITicketRepository ticketRepository,
        IOrganizationMemberRepository organizationMemberRepository,
        ITeamMemberRepository teamMemberRepository,
        IProjectMemberRepository projectMemberRepository,
        ITupleWriter tupleWriter,
        IAuditEventWriter auditWriter,
        ILogger<OpenFgaBackfillJob> logger)
    {
        ArgumentNullException.ThrowIfNull(organizationRepository);
        ArgumentNullException.ThrowIfNull(teamRepository);
        ArgumentNullException.ThrowIfNull(projectRepository);
        ArgumentNullException.ThrowIfNull(ticketRepository);
        ArgumentNullException.ThrowIfNull(organizationMemberRepository);
        ArgumentNullException.ThrowIfNull(teamMemberRepository);
        ArgumentNullException.ThrowIfNull(projectMemberRepository);
        ArgumentNullException.ThrowIfNull(tupleWriter);
        ArgumentNullException.ThrowIfNull(auditWriter);
        ArgumentNullException.ThrowIfNull(logger);
        _organizationRepository = organizationRepository;
        _teamRepository = teamRepository;
        _projectRepository = projectRepository;
        _ticketRepository = ticketRepository;
        _organizationMemberRepository = organizationMemberRepository;
        _teamMemberRepository = teamMemberRepository;
        _projectMemberRepository = projectMemberRepository;
        _tupleWriter = tupleWriter;
        _auditWriter = auditWriter;
        _logger = logger;
    }

    /// <summary>Result of a single backfill run.</summary>
    public sealed record BackfillResult(
        int OrganizationsScanned,
        int TeamsScanned,
        int ProjectsScanned,
        int MembershipsScanned,
        int TicketsScanned,
        int TuplesWritten,
        int TuplesFailed);

    /// <summary>Runs the backfill end to end.</summary>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>A <see cref="BackfillResult"/> with per-category counts.</returns>
    public async Task<BackfillResult> RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("OpenFGA backfill starting.");

        int orgs = 0;
        int teams = 0;
        int projects = 0;
        int memberships = 0;
        int tickets = 0;
        int written = 0;
        int failed = 0;

        List<TupleKey> buffer = new(MaxTuplesPerWrite);

        // 1. Organizations + their members.
        IReadOnlyList<Organization> orgList = await _organizationRepository
            .GetAllAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (Organization org in orgList)
        {
            orgs++;

            IReadOnlyList<OrganizationMember> orgMembers = await _organizationMemberRepository
                .GetByParentAsync(org.Id, cancellationToken)
                .ConfigureAwait(false);
            foreach (OrganizationMember member in orgMembers)
            {
                memberships++;
                buffer.Add(TupleShapes.OrgMemberFromRole(org.Id, member.UserId, member.Role));
                (int w, int f) = await FlushIfFullAsync(buffer, cancellationToken).ConfigureAwait(false);
                written += w;
                failed += f;
            }
        }

        // 2. Teams: parent_org tuple + members + iterate projects under each team.
        IReadOnlyList<Team> teamList = await _teamRepository
            .GetAllAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        foreach (Team team in teamList)
        {
            teams++;
            buffer.Add(TupleShapes.TeamParentOrg(team.Id, team.OrganizationId));
            {
                (int w, int f) = await FlushIfFullAsync(buffer, cancellationToken).ConfigureAwait(false);
                written += w;
                failed += f;
            }

            IReadOnlyList<TeamMember> teamMembers = await _teamMemberRepository
                .GetByParentAsync(team.Id, cancellationToken)
                .ConfigureAwait(false);
            foreach (TeamMember member in teamMembers)
            {
                memberships++;
                buffer.Add(TupleShapes.TeamAdminFromRole(team.Id, member.UserId, member.Role));
                (int w, int f) = await FlushIfFullAsync(buffer, cancellationToken).ConfigureAwait(false);
                written += w;
                failed += f;
            }

            IReadOnlyList<Project> teamProjects = await _projectRepository
                .GetByTeamAsync(team.Id, cancellationToken)
                .ConfigureAwait(false);
            foreach (Project project in teamProjects)
            {
                projects++;
                buffer.Add(TupleShapes.ProjectParentTeam(project.Id, team.Id));
                {
                    (int w, int f) = await FlushIfFullAsync(buffer, cancellationToken).ConfigureAwait(false);
                    written += w;
                    failed += f;
                }

                IReadOnlyList<ProjectMember> projectMembers = await _projectMemberRepository
                    .GetByParentAsync(project.Id, cancellationToken)
                    .ConfigureAwait(false);
                foreach (ProjectMember member in projectMembers)
                {
                    memberships++;
                    buffer.Add(
                        TupleShapes.ProjectMemberFromRole(project.Id, member.UserId, member.Role));
                    (int w, int f) = await FlushIfFullAsync(buffer, cancellationToken).ConfigureAwait(false);
                    written += w;
                    failed += f;
                }
            }
        }

        // 3. Tickets: parent_project + reporter + optional assignee.
        IReadOnlyList<Ticket> ticketList = await _ticketRepository
            .GetAllAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        foreach (Ticket ticket in ticketList)
        {
            tickets++;
            buffer.Add(TupleShapes.TicketParentProject(ticket.Id, ticket.ProjectId));
            buffer.Add(TupleShapes.TicketReporter(ticket.Id, ticket.ReporterId));
            if (ticket.AssigneeId.HasValue)
            {
                buffer.Add(TupleShapes.TicketAssignee(ticket.Id, ticket.AssigneeId.Value));
            }

            (int wt, int ft) = await FlushIfFullAsync(buffer, cancellationToken).ConfigureAwait(false);
            written += wt;
            failed += ft;
        }

        // Final flush.
        (int finalWritten, int finalFailed) = await FlushAsync(buffer, cancellationToken).ConfigureAwait(false);
        written += finalWritten;
        failed += finalFailed;

        BackfillResult result = new(
            OrganizationsScanned: orgs,
            TeamsScanned: teams,
            ProjectsScanned: projects,
            MembershipsScanned: memberships,
            TicketsScanned: tickets,
            TuplesWritten: written,
            TuplesFailed: failed);

        _logger.LogInformation(
            "OpenFGA backfill completed. Orgs={Orgs} Teams={Teams} Projects={Projects} Memberships={Memberships} Tickets={Tickets} TuplesWritten={Tuples} TuplesFailed={Failed}",
            orgs,
            teams,
            projects,
            memberships,
            tickets,
            written,
            failed);

        await WriteCompletionAuditAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<(int Written, int Failed)> FlushIfFullAsync(
        List<TupleKey> buffer,
        CancellationToken cancellationToken)
    {
        if (buffer.Count < MaxTuplesPerWrite)
        {
            return (0, 0);
        }

        return await FlushAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Flushes the buffered tuples through <see cref="ITupleWriter"/>.
    /// Returns the per-chunk (written, failed) tuple — written counts the
    /// tuples successfully accepted by the writer, failed counts tuples in a
    /// chunk whose underlying write threw a non-cancellation exception.
    /// </summary>
    private async Task<(int Written, int Failed)> FlushAsync(
        List<TupleKey> buffer,
        CancellationToken cancellationToken)
    {
        if (buffer.Count == 0)
        {
            return (0, 0);
        }

        TupleKey[] writes = buffer.ToArray();
        buffer.Clear();

        try
        {
            await _tupleWriter
                .WriteAsync(writes, Array.Empty<TupleKey>(), cancellationToken)
                .ConfigureAwait(false);
            return (writes.Length, 0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // ITupleWriter swallows by contract — but defend against future contract
            // changes. The chunk's already lost; let the next chunk continue.
            _logger.LogWarning(
                ex,
                "OpenFGA backfill chunk failed; continuing. ChunkSize={ChunkSize}",
                writes.Length);
            return (0, writes.Length);
        }
    }

    private async Task WriteCompletionAuditAsync(
        BackfillResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            AuditEvent auditEvent = new()
            {
                EventType = "openfga_backfill_completed",
                PayloadJson = JsonSerializer.Serialize(result, AuditPayloadJsonOptions),
            };

            await _auditWriter.WriteAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to record openfga_backfill_completed audit event; result already in logs.");
        }
    }
}
