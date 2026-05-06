using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Heimdall.DAL.Configuration;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Heimdall.Web.Bootstrap;

/// <summary>
/// Idempotent runtime backfill that populates the new <c>tickets</c> FK columns
/// (<c>project_id</c>, <c>team_id</c>, <c>reporter_id</c>) on legacy rows so the
/// matching NOT NULL flips in <c>M202605050022</c> / <c>M202605050024</c> succeed.
/// Mirrors <see cref="DefaultHierarchyBootstrapper"/> in style: deny-closed on
/// missing prerequisites, transient DB failures swallowed, cooperative cancellation
/// honoured. Phase 2.4 / 2.5 of <c>docs/proposals/team-collaboration.md</c> §4.
/// </summary>
/// <remarks>
/// <para>
/// The matching FluentMigrator migrations add the columns nullable (steps 10, 11, 13),
/// then this backfiller populates them, then a follow-up migration (steps 12, 14)
/// flips them to NOT NULL. Doing the backfill in SQL would require the migration to
/// know the bootstrap admin's UUID, which does not exist at migrate-time on a fresh
/// database — exactly the same constraint that forced
/// <see cref="DefaultHierarchyBootstrapper"/> to live at runtime.
/// </para>
/// <para>
/// <c>tickets.assignee_id</c> is intentionally not touched — <c>NULL</c> is a real
/// ticket state ("unassigned") per <c>docs/proposals/team-collaboration.md</c> §5.3.
/// </para>
/// <para>
/// Failure policy matches <see cref="DefaultHierarchyBootstrapper"/>: a transient DB
/// outage at boot must not abort startup, so generic exceptions are logged and
/// swallowed; <see cref="OperationCanceledException"/> propagates so cooperative host
/// shutdown is honoured.
/// </para>
/// </remarks>
public sealed class TicketDefaultsBackfiller
{
    private readonly UserManager<HeimdallUser> _userManager;
    private readonly IOrganizationRepository _organizationRepository;
    private readonly ITeamRepository _teamRepository;
    private readonly IProjectRepository _projectRepository;
    private readonly DataOptions _options;
    private readonly ILogger<TicketDefaultsBackfiller> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TicketDefaultsBackfiller"/> class.
    /// </summary>
    /// <param name="userManager">Identity user manager used to look up the bootstrap admin by email.</param>
    /// <param name="organizationRepository">Organization persistence — supplies the seed-org slug lookup.</param>
    /// <param name="teamRepository">Team persistence — supplies the per-org seed-team slug lookup.</param>
    /// <param name="projectRepository">Project persistence — supplies the per-team seed-project slug lookup.</param>
    /// <param name="options">Bound <see cref="DataOptions"/> providing the Postgres connection string for the bulk UPDATEs.</param>
    /// <param name="logger">Structured logger.</param>
    /// <exception cref="ArgumentNullException">If any argument is <c>null</c>.</exception>
    public TicketDefaultsBackfiller(
        UserManager<HeimdallUser> userManager,
        IOrganizationRepository organizationRepository,
        ITeamRepository teamRepository,
        IProjectRepository projectRepository,
        IOptions<DataOptions> options,
        ILogger<TicketDefaultsBackfiller> logger)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(organizationRepository);
        ArgumentNullException.ThrowIfNull(teamRepository);
        ArgumentNullException.ThrowIfNull(projectRepository);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _userManager = userManager;
        _organizationRepository = organizationRepository;
        _teamRepository = teamRepository;
        _projectRepository = projectRepository;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Runs the ticket-defaults backfill. If the bootstrap admin or the default
    /// org/team/project cannot be resolved, logs a warning and returns without
    /// touching <c>tickets</c>. Idempotent: each <c>UPDATE</c> is keyed on
    /// <c>… IS NULL</c>, so re-runs against a fully-backfilled table are no-ops.
    /// </summary>
    /// <param name="bootstrapAdminEmail">
    /// Email of the bootstrap admin (typically <c>HEIMDALL_BOOTSTRAP_ADMIN_EMAIL</c>).
    /// Used to resolve the user-id that legacy <c>tickets.reporter_id</c> rows fall
    /// back to.
    /// </param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    public async Task RunAsync(string? bootstrapAdminEmail, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bootstrapAdminEmail))
        {
            _logger.LogInformation(
                "Ticket-defaults backfill skipped: HEIMDALL_BOOTSTRAP_ADMIN_EMAIL not set.");
            return;
        }

        try
        {
            HeimdallUser? admin = await _userManager
                .FindByEmailAsync(bootstrapAdminEmail)
                .ConfigureAwait(false);
            if (admin is null)
            {
                _logger.LogWarning(
                    "Ticket-defaults backfill skipped: bootstrap admin not found by email.");
                return;
            }

            Organization? org = await _organizationRepository
                .GetBySlugAsync(DefaultHierarchyBootstrapper.OrganizationSlug, cancellationToken)
                .ConfigureAwait(false);
            if (org is null)
            {
                _logger.LogWarning(
                    "Ticket-defaults backfill skipped: seed organization (slug={Slug}) not found.",
                    DefaultHierarchyBootstrapper.OrganizationSlug);
                return;
            }

            Team? team = await _teamRepository
                .GetBySlugAsync(org.Id, DefaultHierarchyBootstrapper.TeamSlug, cancellationToken)
                .ConfigureAwait(false);
            if (team is null)
            {
                _logger.LogWarning(
                    "Ticket-defaults backfill skipped: seed team (slug={Slug}) not found in organization {OrganizationId}.",
                    DefaultHierarchyBootstrapper.TeamSlug,
                    org.Id);
                return;
            }

            Project? project = await _projectRepository
                .GetBySlugAsync(team.Id, DefaultHierarchyBootstrapper.ProjectSlug, cancellationToken)
                .ConfigureAwait(false);
            if (project is null)
            {
                _logger.LogWarning(
                    "Ticket-defaults backfill skipped: seed project (slug={Slug}) not found in team {TeamId}.",
                    DefaultHierarchyBootstrapper.ProjectSlug,
                    team.Id);
                return;
            }

            await BackfillAsync(project.Id, team.Id, admin.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cooperative cancellation must propagate — host shutdown depends on it.
            throw;
        }
        catch (NpgsqlException ex)
        {
            // Match DefaultHierarchyBootstrapper's stance: a transient DB hiccup at
            // boot must not take the whole app down. The matching NOT NULL flip
            // migrations on the next deploy will surface any rows we failed to
            // backfill (they will fail loudly), which is the intended safety net.
            _logger.LogWarning(
                ex,
                "Ticket-defaults backfill failed with a Postgres error; continuing startup without backfill.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "Ticket-defaults backfill failed unexpectedly; continuing startup without backfill.");
        }
    }

    private async Task BackfillAsync(
        Guid projectId,
        Guid teamId,
        Guid adminId,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_options.PostgresConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Three independent UPDATEs keyed on "… IS NULL" — each is idempotent on its
        // own and re-runs against a fully-backfilled table affect zero rows. assignee_id
        // is intentionally NOT touched: NULL is a real ticket state per §5.3.
        int projectRows = await connection
            .ExecuteAsync(
                new CommandDefinition(
                    "UPDATE tickets SET project_id = @ProjectId WHERE project_id IS NULL;",
                    new { ProjectId = projectId },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        int teamRows = await connection
            .ExecuteAsync(
                new CommandDefinition(
                    "UPDATE tickets SET team_id = @TeamId WHERE team_id IS NULL;",
                    new { TeamId = teamId },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        int reporterRows = await connection
            .ExecuteAsync(
                new CommandDefinition(
                    "UPDATE tickets SET reporter_id = @AdminId WHERE reporter_id IS NULL;",
                    new { AdminId = adminId },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Ticket-defaults backfill completed (project_id={ProjectRows}, team_id={TeamRows}, reporter_id={ReporterRows} rows updated).",
            projectRows,
            teamRows,
            reporterRows);
    }
}
