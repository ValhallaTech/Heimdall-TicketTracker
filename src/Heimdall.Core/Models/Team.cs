using System;

namespace Heimdall.Core.Models;

/// <summary>
/// Domain entity representing a team within an <see cref="Organization"/>. Mirrors
/// the <c>teams</c> table created by <c>M202605050011_CreateTeams</c>. Slug uniqueness
/// is scoped per-organization (two orgs can both have a team named <c>platform</c>).
/// </summary>
public class Team
{
    /// <summary>Gets or sets the unique identifier (Postgres <c>uuid</c>).</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the parent organization id.</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>Gets or sets the URL-friendly identifier. Unique within the parent organization.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Gets or sets the human-readable display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the UTC offset timestamp at which the team was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the id of the user that created the team.</summary>
    public Guid CreatedBy { get; set; }
}
