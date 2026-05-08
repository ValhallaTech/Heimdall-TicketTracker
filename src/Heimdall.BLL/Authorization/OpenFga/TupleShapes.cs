using System;
using Heimdall.Core.Models;

namespace Heimdall.BLL.Authorization.OpenFga;

/// <summary>
/// Canonical helpers that translate Heimdall's relational rows into the OpenFGA
/// tuple shapes from <c>docs/proposals/openfga-input-contract.md</c> §2 + §3. One
/// method per row of the contract; the role-collapse decisions in §3 (owner→admin
/// for org/project, manager+team_lead→team#admin) are baked in here so the hook
/// authors and the backfill author write them once and identically.
/// </summary>
/// <remarks>
/// <para>
/// All ids are rendered with invariant culture and the standard <c>Guid.ToString("D")</c>
/// (lowercase 36-character hex) form so the same row always produces the same tuple
/// key on any thread / locale — a load-bearing invariant for OpenFGA tuple equality
/// across the round trip with the sidecar.
/// </para>
/// </remarks>
public static class TupleShapes
{
    /// <summary>OpenFGA object-type prefix for users.</summary>
    public const string UserType = "user";

    /// <summary>OpenFGA object-type prefix for organizations.</summary>
    public const string OrganizationType = "organization";

    /// <summary>OpenFGA object-type prefix for teams.</summary>
    public const string TeamType = "team";

    /// <summary>OpenFGA object-type prefix for projects.</summary>
    public const string ProjectType = "project";

    /// <summary>OpenFGA object-type prefix for tickets.</summary>
    public const string TicketType = "ticket";

    /// <summary>Relation name: <c>admin</c>.</summary>
    public const string AdminRelation = "admin";

    /// <summary>Relation name: <c>member</c>.</summary>
    public const string MemberRelation = "member";

    /// <summary>Relation name: <c>viewer</c>.</summary>
    public const string ViewerRelation = "viewer";

    /// <summary>Relation name: <c>parent_org</c>.</summary>
    public const string ParentOrgRelation = "parent_org";

    /// <summary>Relation name: <c>parent_team</c>.</summary>
    public const string ParentTeamRelation = "parent_team";

    /// <summary>Relation name: <c>parent_project</c>.</summary>
    public const string ParentProjectRelation = "parent_project";

    /// <summary>Relation name: <c>reporter</c>.</summary>
    public const string ReporterRelation = "reporter";

    /// <summary>Relation name: <c>assignee</c>.</summary>
    public const string AssigneeRelation = "assignee";

    /// <summary>Renders <c>user:{guid}</c> in canonical lowercase form.</summary>
    public static string UserRef(Guid userId) =>
        FormattableString.Invariant($"{UserType}:{userId:D}");

    /// <summary>Renders <c>organization:{guid}</c>.</summary>
    public static string OrganizationRef(Guid organizationId) =>
        FormattableString.Invariant($"{OrganizationType}:{organizationId:D}");

    /// <summary>Renders <c>team:{guid}</c>.</summary>
    public static string TeamRef(Guid teamId) =>
        FormattableString.Invariant($"{TeamType}:{teamId:D}");

    /// <summary>Renders <c>project:{guid}</c>.</summary>
    public static string ProjectRef(Guid projectId) =>
        FormattableString.Invariant($"{ProjectType}:{projectId:D}");

    /// <summary>Renders <c>ticket:{int}</c> (invariant culture).</summary>
    public static string TicketRef(int ticketId) =>
        FormattableString.Invariant($"{TicketType}:{ticketId}");

    /// <summary>
    /// <c>organization#admin</c> tuple for the given user. Use when the row's wire
    /// role has already been collapsed; otherwise prefer <see cref="OrgMemberFromRole"/>.
    /// </summary>
    public static TupleKey OrgAdmin(Guid orgId, Guid userId) =>
        new(UserRef(userId), AdminRelation, OrganizationRef(orgId));

    /// <summary><c>organization#member</c> tuple for the given user.</summary>
    public static TupleKey OrgMember(Guid orgId, Guid userId) =>
        new(UserRef(userId), MemberRelation, OrganizationRef(orgId));

    /// <summary>
    /// Maps an <c>organization_members.role</c> wire string to the matching
    /// organization tuple per the input contract:
    /// <c>owner|admin → organization#admin</c>; <c>member|viewer → organization#member</c>.
    /// </summary>
    /// <exception cref="ArgumentException">If <paramref name="role"/> is not a recognised wire string.</exception>
    public static TupleKey OrgMemberFromRole(Guid orgId, Guid userId, string role)
    {
        ArgumentNullException.ThrowIfNull(role);
        return role.ToLowerInvariant() switch
        {
            "owner" or "admin" => OrgAdmin(orgId, userId),
            "member" or "viewer" => OrgMember(orgId, userId),
            _ => throw new ArgumentException(
                $"'{role}' is not a recognised org_member_role wire value.",
                nameof(role)),
        };
    }

    /// <summary><c>team#admin</c> tuple for the given user.</summary>
    public static TupleKey TeamAdmin(Guid teamId, Guid userId) =>
        new(UserRef(userId), AdminRelation, TeamRef(teamId));

    /// <summary><c>team#member</c> tuple for the given user.</summary>
    public static TupleKey TeamMember(Guid teamId, Guid userId) =>
        new(UserRef(userId), MemberRelation, TeamRef(teamId));

    /// <summary><c>team#viewer</c> tuple for the given user.</summary>
    public static TupleKey TeamViewer(Guid teamId, Guid userId) =>
        new(UserRef(userId), ViewerRelation, TeamRef(teamId));

    /// <summary>
    /// Maps a <see cref="TeamMemberRole"/> to the matching team tuple per the
    /// input contract: <c>Manager|TeamLead → team#admin</c>;
    /// <c>Member → team#member</c>; <c>Viewer → team#viewer</c>.
    /// </summary>
    public static TupleKey TeamAdminFromRole(Guid teamId, Guid userId, TeamMemberRole role) =>
        role switch
        {
            TeamMemberRole.Manager or TeamMemberRole.TeamLead => TeamAdmin(teamId, userId),
            TeamMemberRole.Member => TeamMember(teamId, userId),
            TeamMemberRole.Viewer => TeamViewer(teamId, userId),
            _ => throw new ArgumentOutOfRangeException(
                nameof(role), role, "Unknown TeamMemberRole value."),
        };

    /// <summary><c>project#admin</c> tuple for the given user.</summary>
    public static TupleKey ProjectAdmin(Guid projectId, Guid userId) =>
        new(UserRef(userId), AdminRelation, ProjectRef(projectId));

    /// <summary><c>project#member</c> tuple for the given user.</summary>
    public static TupleKey ProjectMember(Guid projectId, Guid userId) =>
        new(UserRef(userId), MemberRelation, ProjectRef(projectId));

    /// <summary><c>project#viewer</c> tuple for the given user.</summary>
    public static TupleKey ProjectViewer(Guid projectId, Guid userId) =>
        new(UserRef(userId), ViewerRelation, ProjectRef(projectId));

    /// <summary>
    /// Maps a <c>project_members.role</c> wire string to the matching project tuple
    /// per the input contract: <c>owner|admin → project#admin</c>;
    /// <c>member → project#member</c>; <c>viewer → project#viewer</c>.
    /// </summary>
    /// <exception cref="ArgumentException">If <paramref name="role"/> is not a recognised wire string.</exception>
    public static TupleKey ProjectMemberFromRole(Guid projectId, Guid userId, string role)
    {
        ArgumentNullException.ThrowIfNull(role);
        return role.ToLowerInvariant() switch
        {
            "owner" or "admin" => ProjectAdmin(projectId, userId),
            "member" => ProjectMember(projectId, userId),
            "viewer" => ProjectViewer(projectId, userId),
            _ => throw new ArgumentException(
                $"'{role}' is not a recognised project_member_role wire value.",
                nameof(role)),
        };
    }

    /// <summary><c>team#parent_org</c> tuple — the team-to-organization parent pointer.</summary>
    public static TupleKey TeamParentOrg(Guid teamId, Guid orgId) =>
        new(OrganizationRef(orgId), ParentOrgRelation, TeamRef(teamId));

    /// <summary><c>project#parent_team</c> tuple — the project-to-team parent pointer.</summary>
    public static TupleKey ProjectParentTeam(Guid projectId, Guid teamId) =>
        new(TeamRef(teamId), ParentTeamRelation, ProjectRef(projectId));

    /// <summary><c>ticket#parent_project</c> tuple — the ticket-to-project parent pointer.</summary>
    public static TupleKey TicketParentProject(int ticketId, Guid projectId) =>
        new(ProjectRef(projectId), ParentProjectRelation, TicketRef(ticketId));

    /// <summary><c>ticket#reporter</c> tuple.</summary>
    public static TupleKey TicketReporter(int ticketId, Guid userId) =>
        new(UserRef(userId), ReporterRelation, TicketRef(ticketId));

    /// <summary><c>ticket#assignee</c> tuple.</summary>
    public static TupleKey TicketAssignee(int ticketId, Guid userId) =>
        new(UserRef(userId), AssigneeRelation, TicketRef(ticketId));
}
