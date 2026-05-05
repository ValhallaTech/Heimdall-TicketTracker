using System;

namespace Heimdall.Core.Models;

/// <summary>
/// Domain entity representing the top-level scope in Heimdall's collaboration
/// hierarchy: <c>organization</c> → <c>team</c> → <c>project</c> → <c>ticket</c>.
/// Mirrors the <c>organizations</c> table created by
/// <c>M202605050010_CreateOrganizations</c>. Like <see cref="HeimdallUser"/>, this
/// type has no dependency on infrastructure assemblies so <c>Heimdall.Core</c> stays
/// framework-free.
/// </summary>
public class Organization
{
    /// <summary>Gets or sets the unique identifier (Postgres <c>uuid</c>).</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the URL-friendly identifier. Stored <c>citext</c> and globally unique.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Gets or sets the human-readable display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the UTC offset timestamp at which the organization was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the id of the user that created the organization.</summary>
    public Guid CreatedBy { get; set; }
}
