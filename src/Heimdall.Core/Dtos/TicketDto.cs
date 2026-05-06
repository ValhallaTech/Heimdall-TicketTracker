using System;
using System.ComponentModel.DataAnnotations;
using Heimdall.Core.Models;

namespace Heimdall.Core.Dtos;

/// <summary>
/// Data transfer object for <see cref="Ticket"/> used by the Blazor UI layer.
/// </summary>
public class TicketDto
{
    /// <summary>Gets or sets the unique identifier. Zero for new records.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the short ticket title.</summary>
    [Required, StringLength(200)]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the long-form description of the ticket.</summary>
    [Required, StringLength(4000)]
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the lifecycle status.</summary>
    [Required]
    public TicketStatus Status { get; set; } = TicketStatus.Open;

    /// <summary>Gets or sets the priority level.</summary>
    [Required]
    public TicketPriority Priority { get; set; } = TicketPriority.Medium;

    /// <summary>
    /// Gets or sets the FK to the owning project. Required after Phase 2.4 (NOT NULL on the
    /// underlying column).
    /// </summary>
    [Required]
    [NotEmptyGuid]
    public Guid ProjectId { get; set; }

    /// <summary>
    /// Gets or sets the FK to the owning team. Required after Phase 2.4 (NOT NULL on the
    /// underlying column). Drives the per-team queue filter in
    /// <c>docs/proposals/team-collaboration.md</c> §5.1.
    /// </summary>
    [Required]
    [NotEmptyGuid]
    public Guid TeamId { get; set; }

    /// <summary>
    /// Gets or sets the FK to the reporting user. Required after Phase 2.5 — every ticket
    /// always has a reporter.
    /// </summary>
    [Required]
    [NotEmptyGuid]
    public Guid ReporterId { get; set; }

    /// <summary>
    /// Gets or sets the FK to the assigned user. <c>null</c> represents an unassigned
    /// ticket (a legitimate state per <c>docs/proposals/team-collaboration.md</c> §5.3).
    /// </summary>
    public Guid? AssigneeId { get; set; }

    /// <summary>Gets or sets the UTC offset timestamp when the ticket was created.</summary>
    public DateTimeOffset DateCreated { get; set; }

    /// <summary>Gets or sets the UTC offset timestamp when the ticket was last updated.</summary>
    public DateTimeOffset DateUpdated { get; set; }
}
