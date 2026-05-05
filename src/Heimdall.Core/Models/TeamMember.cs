using System;

namespace Heimdall.Core.Models;

/// <summary>
/// Domain entity representing a user's membership in a <see cref="Team"/>. Mirrors
/// the <c>team_members</c> table created by <c>M202605050014_CreateTeamMembers</c>.
/// The composite primary key is <c>(user_id, team_id)</c>. <see cref="Role"/> is
/// the strongly-typed <see cref="TeamMemberRole"/> enum because Phase 2.6's
/// <c>IPermissionService</c> consumes it as a value type per
/// <c>docs/proposals/team-collaboration.md</c> §6.
/// </summary>
public class TeamMember
{
    /// <summary>Gets or sets the id of the user that holds the membership.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the parent team id.</summary>
    public Guid TeamId { get; set; }

    /// <summary>Gets or sets the team-member role.</summary>
    public TeamMemberRole Role { get; set; }

    /// <summary>Gets or sets the UTC offset timestamp at which the membership was added.</summary>
    public DateTimeOffset AddedAt { get; set; }

    /// <summary>Gets or sets the id of the user that added this membership (preserved via FK RESTRICT).</summary>
    public Guid AddedBy { get; set; }
}
