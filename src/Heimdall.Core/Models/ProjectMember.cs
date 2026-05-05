using System;

namespace Heimdall.Core.Models;

/// <summary>
/// Domain entity representing a user's membership in a <see cref="Project"/>.
/// Mirrors the <c>project_members</c> table created by
/// <c>M202605050015_CreateProjectMembers</c>. The composite primary key is
/// <c>(user_id, project_id)</c>. The <see cref="Role"/> property carries the
/// wire-format role string (one of <c>owner</c>, <c>admin</c>, <c>member</c>,
/// <c>viewer</c>); Phase 2 has no in-process consumer of the role, OpenFGA reads
/// it directly in Phase 3.
/// </summary>
public class ProjectMember
{
    /// <summary>Gets or sets the id of the user that holds the membership.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the parent project id.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>Gets or sets the wire-format role (<c>owner</c>, <c>admin</c>, <c>member</c>, or <c>viewer</c>).</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>Gets or sets the UTC offset timestamp at which the membership was added.</summary>
    public DateTimeOffset AddedAt { get; set; }

    /// <summary>Gets or sets the id of the user that added this membership (preserved via FK RESTRICT).</summary>
    public Guid AddedBy { get; set; }
}
