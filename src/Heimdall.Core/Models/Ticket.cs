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

    /// <summary>Gets or sets the reporter / submitter name.</summary>
    public string Reporter { get; set; } = string.Empty;

    /// <summary>Gets or sets the assignee name (may be empty when unassigned).</summary>
    public string? Assignee { get; set; }

    /// <summary>Gets or sets the UTC offset timestamp when the ticket was created.</summary>
    public DateTimeOffset DateCreated { get; set; }

    /// <summary>Gets or sets the UTC offset timestamp when the ticket was last updated.</summary>
    public DateTimeOffset DateUpdated { get; set; }
}
