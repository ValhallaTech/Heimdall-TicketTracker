namespace Heimdall.Core.Models;

/// <summary>
/// Strongly-typed role for a <see cref="TeamMember"/>. Mirrors the Postgres
/// <c>team_member_role</c> enum created by <c>M202605050014_CreateTeamMembers</c>.
/// The on-the-wire / database representation is snake_case
/// (<c>manager</c>, <c>team_lead</c>, <c>member</c>, <c>viewer</c>); Dapper maps
/// to and from these wire strings via the type handler registered in
/// <c>Heimdall.DAL.Extensions.ServiceCollectionExtensions.AddDal</c>.
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
