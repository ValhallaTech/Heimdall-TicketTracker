using System;

namespace Heimdall.Core.Models;

/// <summary>
/// Domain entity representing a project within a <see cref="Team"/>. Mirrors the
/// <c>projects</c> table created by <c>M202605050012_CreateProjects</c>. Slug
/// uniqueness is scoped per-team. Tickets gain a <c>project_id</c> foreign key in
/// Phase 2.4.
/// </summary>
public class Project
{
    /// <summary>Gets or sets the unique identifier (Postgres <c>uuid</c>).</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the parent team id.</summary>
    public Guid TeamId { get; set; }

    /// <summary>Gets or sets the URL-friendly identifier. Unique within the parent team.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Gets or sets the human-readable display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the UTC offset timestamp at which the project was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the id of the user that created the project.</summary>
    public Guid CreatedBy { get; set; }
}
