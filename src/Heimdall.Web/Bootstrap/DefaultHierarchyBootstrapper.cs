using System;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Heimdall.Web.Bootstrap;

/// <summary>
/// Idempotent default-hierarchy backfill for Phase 2.3 step 9 of
/// <c>docs/proposals/team-collaboration.md</c> §4. Ensures one <c>heimdall</c>
/// organization, one <c>default</c> team in that organization, and one
/// <c>default</c> project in that team always exist after startup, all
/// <c>created_by = bootstrap_admin.id</c>. Also seeds membership rows for the
/// bootstrap admin in <c>organization_members</c> (<c>role = 'owner'</c>),
/// <c>team_members</c> (<see cref="TeamMemberRole.Manager"/>), and
/// <c>project_members</c> (<c>role = 'owner'</c>).
/// </summary>
/// <remarks>
/// <para>
/// The membership rows are the load-bearing piece: per
/// <c>docs/proposals/openfga.md</c> step 8, the OpenFGA tuple backfill reads the
/// <c>*_members</c> tables — <strong>not</strong> <c>created_by</c> — so without
/// these rows the bootstrap admin would own the seed hierarchy on paper but fail
/// every <c>Check()</c> after cutover. <see cref="OrganizationSlug"/>,
/// <see cref="TeamSlug"/>, and <see cref="ProjectSlug"/> are the only hard-coded
/// values; everything else is user-created.
/// </para>
/// <para>
/// This service runs at startup (after <see cref="SystemAdminBootstrapper"/>) rather
/// than as a FluentMigrator migration because <c>bootstrap_admin.id</c> does not
/// exist at migrate-time on a fresh DB — the admin row is created at app boot, and
/// every subsequent boot must observe and tolerate the seeded state. Each step
/// is keyed on its natural composite key (slug for parents, <c>(user_id, parent_id)</c>
/// for memberships) so re-runs on a fully-seeded database are no-ops.
/// </para>
/// <para>
/// Failure policy matches <see cref="SystemAdminBootstrapper"/>: a transient DB
/// outage at boot must not abort startup, so generic exceptions are logged and
/// swallowed; <see cref="OperationCanceledException"/> propagates so cooperative
/// host shutdown is honoured.
/// </para>
/// </remarks>
public sealed class DefaultHierarchyBootstrapper
{
    /// <summary>The hard-coded slug of the seed organization.</summary>
    public const string OrganizationSlug = "heimdall";

    /// <summary>The hard-coded slug of the seed team within the seed organization.</summary>
    public const string TeamSlug = "default";

    /// <summary>The hard-coded slug of the seed project within the seed team.</summary>
    public const string ProjectSlug = "default";

    /// <summary>The wire-format role for the bootstrap admin's organization / project memberships.</summary>
    public const string OwnerRole = "owner";

    private readonly UserManager<HeimdallUser> _userManager;
    private readonly IOrganizationRepository _organizationRepository;
    private readonly ITeamRepository _teamRepository;
    private readonly IProjectRepository _projectRepository;
    private readonly IOrganizationMemberRepository _organizationMemberRepository;
    private readonly ITeamMemberRepository _teamMemberRepository;
    private readonly IProjectMemberRepository _projectMemberRepository;
    private readonly ITupleWriter _tupleWriter;
    private readonly ILogger<DefaultHierarchyBootstrapper> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultHierarchyBootstrapper"/> class.
    /// </summary>
    /// <param name="userManager">Identity user manager used to look up the bootstrap admin by email.</param>
    /// <param name="organizationRepository">Organization persistence — supplies the parent slug lookup and create.</param>
    /// <param name="teamRepository">Team persistence — supplies the per-org slug lookup and create.</param>
    /// <param name="projectRepository">Project persistence — supplies the per-team slug lookup and create.</param>
    /// <param name="organizationMemberRepository">Organization-membership persistence — supplies the composite-key lookup and create.</param>
    /// <param name="teamMemberRepository">Team-membership persistence — supplies the composite-key lookup and create.</param>
    /// <param name="projectMemberRepository">Project-membership persistence — supplies the composite-key lookup and create.</param>
    /// <param name="tupleWriter">
    /// OpenFGA tuple writer used to mirror seed-hierarchy and membership rows into
    /// the authorization store per <c>docs/proposals/openfga.md</c> §3 step 7.
    /// Tuples are emitted unconditionally on every run (whether the row was just
    /// created or already existed) so a partially-seeded database that skipped
    /// tuple emission on a previous boot heals itself; the writer's
    /// log+audit+swallow contract absorbs duplicate-tuple errors as the SDK's
    /// <c>WriteFailedDueToInvalidInput</c> response.
    /// </param>
    /// <param name="logger">Structured logger.</param>
    /// <exception cref="ArgumentNullException">If any argument is <c>null</c>.</exception>
    public DefaultHierarchyBootstrapper(
        UserManager<HeimdallUser> userManager,
        IOrganizationRepository organizationRepository,
        ITeamRepository teamRepository,
        IProjectRepository projectRepository,
        IOrganizationMemberRepository organizationMemberRepository,
        ITeamMemberRepository teamMemberRepository,
        IProjectMemberRepository projectMemberRepository,
        ITupleWriter tupleWriter,
        ILogger<DefaultHierarchyBootstrapper> logger)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(organizationRepository);
        ArgumentNullException.ThrowIfNull(teamRepository);
        ArgumentNullException.ThrowIfNull(projectRepository);
        ArgumentNullException.ThrowIfNull(organizationMemberRepository);
        ArgumentNullException.ThrowIfNull(teamMemberRepository);
        ArgumentNullException.ThrowIfNull(projectMemberRepository);
        ArgumentNullException.ThrowIfNull(tupleWriter);
        ArgumentNullException.ThrowIfNull(logger);

        _userManager = userManager;
        _organizationRepository = organizationRepository;
        _teamRepository = teamRepository;
        _projectRepository = projectRepository;
        _organizationMemberRepository = organizationMemberRepository;
        _teamMemberRepository = teamMemberRepository;
        _projectMemberRepository = projectMemberRepository;
        _tupleWriter = tupleWriter;
        _logger = logger;
    }

    /// <summary>
    /// Runs the default-hierarchy backfill. If <paramref name="bootstrapAdminEmail"/> is
    /// null/whitespace the call is a logged no-op (operators that don't bootstrap an
    /// admin can't backfill memberships either — there is no <c>added_by</c> to attribute
    /// the seed rows to). Any unexpected failure is logged at <c>Error</c> and swallowed
    /// so startup can continue; <see cref="OperationCanceledException"/> is re-thrown so
    /// cooperative cancellation is honoured.
    /// </summary>
    /// <param name="bootstrapAdminEmail">
    /// Email of the bootstrap admin (typically the <c>HEIMDALL_BOOTSTRAP_ADMIN_EMAIL</c>
    /// env var). Used only to look up the admin's user-id via <see cref="UserManager{TUser}.FindByEmailAsync"/>.
    /// </param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    public async Task RunAsync(string? bootstrapAdminEmail, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bootstrapAdminEmail))
        {
            _logger.LogInformation(
                "Default-hierarchy bootstrap skipped: HEIMDALL_BOOTSTRAP_ADMIN_EMAIL not set.");
            return;
        }

        try
        {
            HeimdallUser? admin = await _userManager
                .FindByEmailAsync(bootstrapAdminEmail)
                .ConfigureAwait(false);
            if (admin is null)
            {
                // SystemAdminBootstrapper logs failures with IdentityError detail, so a
                // missing admin here is either (a) the admin env vars are unset (already
                // logged above) or (b) creation failed earlier in startup. Either way
                // there is no user to attribute the seed rows to.
                _logger.LogWarning(
                    "Default-hierarchy bootstrap skipped: bootstrap admin not found by email.");
                return;
            }

            Guid orgId = await EnsureOrganizationAsync(admin.Id, cancellationToken).ConfigureAwait(false);
            Guid teamId = await EnsureTeamAsync(orgId, admin.Id, cancellationToken).ConfigureAwait(false);
            Guid projectId = await EnsureProjectAsync(teamId, admin.Id, cancellationToken).ConfigureAwait(false);

            await EnsureOrganizationMemberAsync(admin.Id, orgId, cancellationToken).ConfigureAwait(false);
            await EnsureTeamMemberAsync(admin.Id, teamId, cancellationToken).ConfigureAwait(false);
            await EnsureProjectMemberAsync(admin.Id, projectId, cancellationToken).ConfigureAwait(false);

            // Always re-emit the parent-of and membership tuples — even when every
            // row pre-existed — so a partial-seed boot that lost tuple writes
            // self-heals on the next run. Duplicate-tuple errors are swallowed by
            // ITupleWriter (logged + audited as openfga_tuple_write_failed).
            await EmitSeedTuplesAsync(admin.Id, orgId, teamId, projectId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cooperative cancellation must propagate — host shutdown depends on it.
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "Default-hierarchy bootstrap failed unexpectedly; continuing startup without backfill.");
        }
    }

    private async Task<Guid> EnsureOrganizationAsync(Guid adminId, CancellationToken cancellationToken)
    {
        Organization? existing = await _organizationRepository
            .GetBySlugAsync(OrganizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.Id;
        }

        var organization = new Organization
        {
            Slug = OrganizationSlug,
            Name = "Heimdall",
            CreatedBy = adminId,
        };
        Guid id = await _organizationRepository
            .CreateAsync(organization, cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "Default-hierarchy bootstrap created seed organization {OrganizationId} (slug={Slug}).",
            id,
            OrganizationSlug);
        return id;
    }

    private async Task<Guid> EnsureTeamAsync(Guid organizationId, Guid adminId, CancellationToken cancellationToken)
    {
        Team? existing = await _teamRepository
            .GetBySlugAsync(organizationId, TeamSlug, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.Id;
        }

        var team = new Team
        {
            OrganizationId = organizationId,
            Slug = TeamSlug,
            Name = "Default",
            CreatedBy = adminId,
        };
        Guid id = await _teamRepository.CreateAsync(team, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Default-hierarchy bootstrap created seed team {TeamId} (slug={Slug}) in organization {OrganizationId}.",
            id,
            TeamSlug,
            organizationId);
        return id;
    }

    private async Task<Guid> EnsureProjectAsync(Guid teamId, Guid adminId, CancellationToken cancellationToken)
    {
        Project? existing = await _projectRepository
            .GetBySlugAsync(teamId, ProjectSlug, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.Id;
        }

        var project = new Project
        {
            TeamId = teamId,
            Slug = ProjectSlug,
            Name = "Default",
            CreatedBy = adminId,
        };
        Guid id = await _projectRepository.CreateAsync(project, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Default-hierarchy bootstrap created seed project {ProjectId} (slug={Slug}) in team {TeamId}.",
            id,
            ProjectSlug,
            teamId);
        return id;
    }

    private async Task EnsureOrganizationMemberAsync(Guid adminId, Guid organizationId, CancellationToken cancellationToken)
    {
        OrganizationMember? existing = await _organizationMemberRepository
            .GetAsync(adminId, organizationId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return;
        }

        await _organizationMemberRepository
            .AddAsync(
                new OrganizationMember
                {
                    UserId = adminId,
                    OrganizationId = organizationId,
                    Role = OwnerRole,
                    AddedBy = adminId,
                },
                cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "Default-hierarchy bootstrap added owner organization-membership for bootstrap admin {UserId} on organization {OrganizationId}.",
            adminId,
            organizationId);
    }

    private async Task EnsureTeamMemberAsync(Guid adminId, Guid teamId, CancellationToken cancellationToken)
    {
        TeamMember? existing = await _teamMemberRepository
            .GetAsync(adminId, teamId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return;
        }

        await _teamMemberRepository
            .AddAsync(
                new TeamMember
                {
                    UserId = adminId,
                    TeamId = teamId,
                    // Manager collapses into team#admin in the OpenFGA mapping
                    // (docs/proposals/team-collaboration.md §4 step 17).
                    Role = TeamMemberRole.Manager,
                    AddedBy = adminId,
                },
                cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "Default-hierarchy bootstrap added manager team-membership for bootstrap admin {UserId} on team {TeamId}.",
            adminId,
            teamId);
    }

    private async Task EnsureProjectMemberAsync(Guid adminId, Guid projectId, CancellationToken cancellationToken)
    {
        ProjectMember? existing = await _projectMemberRepository
            .GetAsync(adminId, projectId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return;
        }

        await _projectMemberRepository
            .AddAsync(
                new ProjectMember
                {
                    UserId = adminId,
                    ProjectId = projectId,
                    Role = OwnerRole,
                    AddedBy = adminId,
                },
                cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "Default-hierarchy bootstrap added owner project-membership for bootstrap admin {UserId} on project {ProjectId}.",
            adminId,
            projectId);
    }

    private async Task EmitSeedTuplesAsync(
        Guid adminId,
        Guid organizationId,
        Guid teamId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        TupleKey[] tuples = new[]
        {
            // Hierarchy parent-of edges.
            TupleShapes.TeamParentOrg(teamId, organizationId),
            TupleShapes.ProjectParentTeam(projectId, teamId),

            // Bootstrap-admin memberships, role-collapsed per the input contract:
            //   organization_members.role = 'owner'      -> organization#admin
            //   team_members.role         = Manager      -> team#admin
            //   project_members.role      = 'owner'      -> project#admin
            TupleShapes.OrgMemberFromRole(organizationId, adminId, OwnerRole),
            TupleShapes.TeamAdminFromRole(teamId, adminId, TeamMemberRole.Manager),
            TupleShapes.ProjectMemberFromRole(projectId, adminId, OwnerRole),
        };

        await _tupleWriter
            .WriteAsync(tuples, Array.Empty<TupleKey>(), cancellationToken)
            .ConfigureAwait(false);
    }
}
