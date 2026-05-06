using System;

namespace Heimdall.Core.Models;

/// <summary>
/// Lifecycle status of a <see cref="Ticket"/>.
/// </summary>
public enum TicketStatus
{
    /// <summary>Ticket has been created but no work has started.</summary>
    Open = 0,

    /// <summary>Ticket is actively being worked on.</summary>
    InProgress = 1,

    /// <summary>Ticket work is complete and pending verification.</summary>
    Resolved = 2,

    /// <summary>Ticket has been closed; no further action required.</summary>
    Closed = 3,
}

/// <summary>
/// Priority levels for a <see cref="Ticket"/>.
/// </summary>
public enum TicketPriority
{
    /// <summary>Lowest priority; can be deferred indefinitely.</summary>
    Low = 0,

    /// <summary>Default priority for typical work items.</summary>
    Medium = 1,

    /// <summary>Should be addressed soon.</summary>
    High = 2,

    /// <summary>Must be addressed immediately.</summary>
    Critical = 3,
}

/// <summary>
/// Domain entity representing a tracked ticket / work item.
/// </summary>
public class Ticket
{
    /// <summary>Gets or sets the unique identifier.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the short ticket title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the long-form description of the ticket.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the lifecycle status.</summary>
    public TicketStatus Status { get; set; } = TicketStatus.Open;

    /// <summary>Gets or sets the priority level.</summary>
    public TicketPriority Priority { get; set; } = TicketPriority.Medium;

    /// <summary>
    /// Gets or sets the FK to the owning <see cref="Project"/> (<c>tickets.project_id</c>,
    /// NOT NULL after <c>M202605050022</c>). Replaces the legacy free-form columns dropped
    /// by <c>M202605050025</c>.
    /// </summary>
    public Guid ProjectId { get; set; }

    /// <summary>
    /// Gets or sets the FK to the owning <see cref="Team"/> (<c>tickets.team_id</c>,
    /// NOT NULL after <c>M202605050022</c>). Drives the per-team queue filter described
    /// in <c>docs/proposals/team-collaboration.md</c> §5.1.
    /// </summary>
    public Guid TeamId { get; set; }

    /// <summary>
    /// Gets or sets the FK to the reporting <see cref="HeimdallUser"/>
    /// (<c>tickets.reporter_id</c>, NOT NULL after <c>M202605050024</c> — every ticket
    /// always has a reporter per <c>docs/proposals/team-collaboration.md</c> §5.3).
    /// </summary>
    public Guid ReporterId { get; set; }

    /// <summary>
    /// Gets or sets the FK to the assigned <see cref="HeimdallUser"/>
    /// (<c>tickets.assignee_id</c>, nullable). <c>null</c> represents the legitimate
    /// "unassigned" state per <c>docs/proposals/team-collaboration.md</c> §5.3.
    /// </summary>
    public Guid? AssigneeId { get; set; }

    /// <summary>Gets or sets the UTC offset timestamp when the ticket was created.</summary>
    public DateTimeOffset DateCreated { get; set; }

    /// <summary>Gets or sets the UTC offset timestamp when the ticket was last updated.</summary>
    public DateTimeOffset DateUpdated { get; set; }
}
