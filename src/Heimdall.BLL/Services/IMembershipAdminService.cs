using System;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Models;

namespace Heimdall.BLL.Services;

/// <summary>
/// Admin-driven membership mutations for the Phase 3.6 admin tuple-management
/// surface (<c>docs/proposals/openfga.md</c> §3 step 11). Each method:
/// </summary>
/// <remarks>
/// <list type="number">
///   <item><description>writes the relational <c>*_members</c> row;</description></item>
///   <item><description>emits a <c>membership_added</c> / <c>membership_removed</c> audit event;</description></item>
///   <item><description>fires the matching OpenFGA tuple write or delete via <see cref="Heimdall.BLL.Authorization.OpenFga.ITupleWriter"/> after the DB row commits — the same post-commit ordering as <c>TicketService.CreateAsync</c> / <c>AssignTicketAsync</c>.</description></item>
/// </list>
/// <para>
/// This is the canonical path for admin-driven tuple changes; the existing
/// tuple-write hooks already cover the create-as-admin paths on org / team /
/// project creation, so callers must <strong>not</strong> bypass this seam to
/// call <see cref="Heimdall.BLL.Authorization.OpenFga.ITupleWriter"/> directly
/// from the Blazor layer.
/// </para>
/// <para>
/// All methods deny-closed on tuple-write failure exactly as the underlying
/// writer does — the DB row remains the source of truth and the backfill job
/// reconciles drift.
/// </para>
/// </remarks>
public interface IMembershipAdminService
{
    /// <summary>Adds an organization membership.</summary>
    /// <param name="organizationId">The parent organization id.</param>
    /// <param name="userId">The user being added.</param>
    /// <param name="role">Wire-format role (one of <c>admin</c>, <c>member</c>).</param>
    /// <param name="actorUserId">The system-admin user performing the change.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    Task AddOrgMemberAsync(
        Guid organizationId,
        Guid userId,
        string role,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Removes an organization membership. Returns <c>true</c> if a row was deleted.</summary>
    Task<bool> RemoveOrgMemberAsync(
        Guid organizationId,
        Guid userId,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Adds a team membership.</summary>
    /// <param name="teamId">The parent team id.</param>
    /// <param name="userId">The user being added.</param>
    /// <param name="role">Strongly-typed team-member role.</param>
    /// <param name="actorUserId">The system-admin user performing the change.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    Task AddTeamMemberAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a team membership. Returns <c>true</c> if a row was deleted.</summary>
    Task<bool> RemoveTeamMemberAsync(
        Guid teamId,
        Guid userId,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Adds a project membership.</summary>
    /// <param name="projectId">The parent project id.</param>
    /// <param name="userId">The user being added.</param>
    /// <param name="role">Wire-format role (one of <c>admin</c>, <c>member</c>, <c>viewer</c>).</param>
    /// <param name="actorUserId">The system-admin user performing the change.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    Task AddProjectMemberAsync(
        Guid projectId,
        Guid userId,
        string role,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a project membership. Returns <c>true</c> if a row was deleted.</summary>
    Task<bool> RemoveProjectMemberAsync(
        Guid projectId,
        Guid userId,
        Guid actorUserId,
        CancellationToken cancellationToken = default);
}
