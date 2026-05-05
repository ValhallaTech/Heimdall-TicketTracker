namespace Heimdall.Core.Models;

/// <summary>
/// Strongly-typed role for a <see cref="TeamMember"/>. Mirrors the Postgres
/// <c>team_member_role</c> enum created by <c>M202605050014_CreateTeamMembers</c>.
/// The on-the-wire / database representation is snake_case
/// (<c>manager</c>, <c>team_lead</c>, <c>member</c>, <c>viewer</c>);
/// <c>TeamMemberRepository</c> bridges to that representation explicitly via
/// <c>TeamMemberRoleConverter.ToWireString</c> on writes (combined with a
/// <c>::team_member_role</c> SQL cast) and an internal row DTO plus
/// <c>TeamMemberRoleConverter.ParseWireString</c> on reads. A custom
/// <c>SqlMapper.TypeHandler&lt;TeamMemberRole&gt;</c> is intentionally NOT used
/// because Dapper short-circuits enum-typed parameter properties around
/// registered handlers.
/// </summary>
public enum TeamMemberRole
{
    /// <summary>The team manager. Wire value: <c>manager</c>.</summary>
    Manager = 0,

    /// <summary>The team lead. Wire value: <c>team_lead</c>.</summary>
    TeamLead = 1,

    /// <summary>A regular team member. Wire value: <c>member</c>.</summary>
    Member = 2,

    /// <summary>A read-only observer. Wire value: <c>viewer</c>.</summary>
    Viewer = 3,
}
