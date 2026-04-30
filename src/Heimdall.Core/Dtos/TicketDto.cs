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

    /// <summary>Gets or sets the reporter / submitter name.</summary>
    [Required, StringLength(100)]
    public string Reporter { get; set; } = string.Empty;

    /// <summary>Gets or sets the assignee name (may be empty when unassigned).</summary>
    [StringLength(100)]
    public string? Assignee { get; set; }

    /// <summary>Gets or sets the UTC offset timestamp when the ticket was created.</summary>
    public DateTimeOffset DateCreated { get; set; }

    /// <summary>Gets or sets the UTC offset timestamp when the ticket was last updated.</summary>
    public DateTimeOffset DateUpdated { get; set; }
}
