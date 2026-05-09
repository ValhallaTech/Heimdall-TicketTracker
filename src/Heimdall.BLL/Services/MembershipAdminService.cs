using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.Core.Auditing;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Microsoft.Extensions.Logging;

namespace Heimdall.BLL.Services;

/// <summary>
/// Default <see cref="IMembershipAdminService"/>. Routes admin tuple-relevant
/// writes through the existing <see cref="ITupleWriter"/> seam so the OpenFGA
/// failure-mode contract (log + audit + swallow + backfill reconcile) is
/// preserved end-to-end (<c>docs/proposals/openfga.md</c> §3 step 11).
/// </summary>
/// <remarks>
/// Mirrors <c>TicketService.CreateAsync</c>'s ordering: relational write →
/// audit row → tuple write. The first two are not wrapped in a single
/// transaction in this surface — admin membership changes are single-row
/// inserts/deletes plus an append-only audit row, and the dual-write
/// trade-off documented in <c>openfga.md</c> §3 step 7 covers the tuple side.
/// </remarks>
public sealed class MembershipAdminService : IMembershipAdminService
{
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IOrganizationMemberRepository _orgMembers;
    private readonly ITeamMemberRepository _teamMembers;
    private readonly IProjectMemberRepository _projectMembers;
    private readonly IAuditEventWriter _auditWriter;
    private readonly ITupleWriter _tupleWriter;
    private readonly ILogger<MembershipAdminService> _logger;

    /// <summary>Initializes a new instance.</summary>
    public MembershipAdminService(
        IOrganizationMemberRepository orgMembers,
        ITeamMemberRepository teamMembers,
        IProjectMemberRepository projectMembers,
        IAuditEventWriter auditWriter,
        ITupleWriter tupleWriter,
        ILogger<MembershipAdminService> logger)
    {
        ArgumentNullException.ThrowIfNull(orgMembers);
        ArgumentNullException.ThrowIfNull(teamMembers);
        ArgumentNullException.ThrowIfNull(projectMembers);
        ArgumentNullException.ThrowIfNull(auditWriter);
        ArgumentNullException.ThrowIfNull(tupleWriter);
        ArgumentNullException.ThrowIfNull(logger);
        _orgMembers = orgMembers;
        _teamMembers = teamMembers;
        _projectMembers = projectMembers;
        _auditWriter = auditWriter;
        _tupleWriter = tupleWriter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task AddOrgMemberAsync(
        Guid organizationId,
        Guid userId,
        string role,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);

        // Validate the wire role up front so a typo in the UI surfaces here
        // rather than via a bare PostgresException from the CHECK constraint
        // in M202605050013_CreateOrganizationMembers.
        string normalized = NormalizeOrgOrProjectRole(role);

        OrganizationMember member = new()
        {
            UserId = userId,
            OrganizationId = organizationId,
            Role = normalized,
            AddedAt = DateTimeOffset.UtcNow,
            AddedBy = actorUserId,
        };
        await _orgMembers.AddAsync(member, cancellationToken).ConfigureAwait(false);

        await WriteAuditAsync(
            "membership_added",
            actorUserId,
            target: organizationId.ToString("D", CultureInfo.InvariantCulture),
            scope: "organization",
            scopeId: organizationId,
            userId: userId,
            role: normalized,
            cancellationToken).ConfigureAwait(false);

        TupleKey tuple = TupleShapes.OrgMemberFromRole(organizationId, userId, normalized);
        await _tupleWriter.WriteAsync(tuple, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Admin {Actor} added user {User} to organization {Org} as {Role}.",
            actorUserId, userId, organizationId, normalized);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveOrgMemberAsync(
        Guid organizationId,
        Guid userId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        // Resolve the existing role first so the tuple delete targets the
        // correct relation (admin vs member). The repo returns null on miss
        // so callers can short-circuit a no-op remove.
        OrganizationMember? existing = await _orgMembers
            .GetAsync(userId, organizationId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        bool removed = await _orgMembers
            .RemoveAsync(userId, organizationId, cancellationToken)
            .ConfigureAwait(false);
        if (!removed)
        {
            return false;
        }

        await WriteAuditAsync(
            "membership_removed",
            actorUserId,
            target: organizationId.ToString("D", CultureInfo.InvariantCulture),
            scope: "organization",
            scopeId: organizationId,
            userId: userId,
            role: existing.Role,
            cancellationToken).ConfigureAwait(false);

        TupleKey tuple = TupleShapes.OrgMemberFromRole(organizationId, userId, existing.Role);
        await _tupleWriter
            .WriteAsync(Array.Empty<TupleKey>(), new[] { tuple }, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Admin {Actor} removed user {User} from organization {Org}.",
            actorUserId, userId, organizationId);
        return true;
    }

    /// <inheritdoc />
    public async Task AddTeamMemberAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        TeamMember member = new()
        {
            UserId = userId,
            TeamId = teamId,
            Role = role,
            AddedAt = DateTimeOffset.UtcNow,
            AddedBy = actorUserId,
        };
        await _teamMembers.AddAsync(member, cancellationToken).ConfigureAwait(false);

        await WriteAuditAsync(
            "membership_added",
            actorUserId,
            target: teamId.ToString("D", CultureInfo.InvariantCulture),
            scope: "team",
            scopeId: teamId,
            userId: userId,
            role: role.ToString(),
            cancellationToken).ConfigureAwait(false);

        TupleKey tuple = TupleShapes.TeamAdminFromRole(teamId, userId, role);
        await _tupleWriter.WriteAsync(tuple, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Admin {Actor} added user {User} to team {Team} as {Role}.",
            actorUserId, userId, teamId, role);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveTeamMemberAsync(
        Guid teamId,
        Guid userId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        TeamMember? existing = await _teamMembers
            .GetAsync(userId, teamId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        bool removed = await _teamMembers
            .RemoveAsync(userId, teamId, cancellationToken)
            .ConfigureAwait(false);
        if (!removed)
        {
            return false;
        }

        await WriteAuditAsync(
            "membership_removed",
            actorUserId,
            target: teamId.ToString("D", CultureInfo.InvariantCulture),
            scope: "team",
            scopeId: teamId,
            userId: userId,
            role: existing.Role.ToString(),
            cancellationToken).ConfigureAwait(false);

        TupleKey tuple = TupleShapes.TeamAdminFromRole(teamId, userId, existing.Role);
        await _tupleWriter
            .WriteAsync(Array.Empty<TupleKey>(), new[] { tuple }, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Admin {Actor} removed user {User} from team {Team}.",
            actorUserId, userId, teamId);
        return true;
    }

    /// <inheritdoc />
    public async Task AddProjectMemberAsync(
        Guid projectId,
        Guid userId,
        string role,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);
        string normalized = NormalizeOrgOrProjectRole(role);

        ProjectMember member = new()
        {
            UserId = userId,
            ProjectId = projectId,
            Role = normalized,
            AddedAt = DateTimeOffset.UtcNow,
            AddedBy = actorUserId,
        };
        await _projectMembers.AddAsync(member, cancellationToken).ConfigureAwait(false);

        await WriteAuditAsync(
            "membership_added",
            actorUserId,
            target: projectId.ToString("D", CultureInfo.InvariantCulture),
            scope: "project",
            scopeId: projectId,
            userId: userId,
            role: normalized,
            cancellationToken).ConfigureAwait(false);

        TupleKey tuple = TupleShapes.ProjectMemberFromRole(projectId, userId, normalized);
        await _tupleWriter.WriteAsync(tuple, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Admin {Actor} added user {User} to project {Project} as {Role}.",
            actorUserId, userId, projectId, normalized);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveProjectMemberAsync(
        Guid projectId,
        Guid userId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        ProjectMember? existing = await _projectMembers
            .GetAsync(userId, projectId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        bool removed = await _projectMembers
            .RemoveAsync(userId, projectId, cancellationToken)
            .ConfigureAwait(false);
        if (!removed)
        {
            return false;
        }

        await WriteAuditAsync(
            "membership_removed",
            actorUserId,
            target: projectId.ToString("D", CultureInfo.InvariantCulture),
            scope: "project",
            scopeId: projectId,
            userId: userId,
            role: existing.Role,
            cancellationToken).ConfigureAwait(false);

        TupleKey tuple = TupleShapes.ProjectMemberFromRole(projectId, userId, existing.Role);
        await _tupleWriter
            .WriteAsync(Array.Empty<TupleKey>(), new[] { tuple }, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Admin {Actor} removed user {User} from project {Project}.",
            actorUserId, userId, projectId);
        return true;
    }

    /// <summary>
    /// Normalizes the user-supplied wire-format role to the canonical lowercase
    /// form. Throws <see cref="ArgumentException"/> for unknown values so a typo
    /// in the UI cannot reach <see cref="TupleShapes"/> (which would surface a
    /// less actionable error).
    /// </summary>
    private static string NormalizeOrgOrProjectRole(string role)
    {
        string lowered = role.ToLowerInvariant();
        return lowered switch
        {
            "owner" or "admin" or "member" or "viewer" => lowered,
            _ => throw new ArgumentException(
                $"'{role}' is not a recognised wire-format role.", nameof(role)),
        };
    }

    private async Task WriteAuditAsync(
        string eventType,
        Guid actorUserId,
        string target,
        string scope,
        Guid scopeId,
        Guid userId,
        string role,
        CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(
            new
            {
                scope,
                scope_id = scopeId,
                user_id = userId,
                role,
            },
            PayloadOptions);

        AuditEvent evt = new()
        {
            ActorUserId = actorUserId,
            EventType = eventType,
            Target = target,
            PayloadJson = payload,
        };
        await _auditWriter.WriteAsync(evt, cancellationToken).ConfigureAwait(false);
    }
}
