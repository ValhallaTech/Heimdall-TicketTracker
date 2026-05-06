using System;

namespace Heimdall.Core.Auditing;

/// <summary>
/// Read-side projection of a single <c>audit_events</c> row used by the admin
/// audit feed (Phase 2.8 step 25 of <c>docs/proposals/team-collaboration.md</c>
/// §7). Distinct from <see cref="AuditEvent"/> — that type is the write-side
/// shape and intentionally omits server-generated columns (<c>id</c>,
/// <c>occurred_at</c>); this type carries them so the UI can render the feed
/// without an extra round-trip.
/// </summary>
public sealed class AuditEventRecord
{
    /// <summary>Gets or sets the audit-row primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the UTC timestamp the row was committed (DB clock).</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Gets or sets the actor user id, or <c>null</c> for anonymous events.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>Gets or sets the dotted event name (e.g. <c>ticket_routed</c>).</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional target identifier the event acted on.</summary>
    public string? Target { get; set; }

    /// <summary>Gets or sets the client IP address as a string, or <c>null</c>.</summary>
    public string? Ip { get; set; }

    /// <summary>Gets or sets the client User-Agent string, or <c>null</c>.</summary>
    public string? UserAgent { get; set; }

    /// <summary>Gets or sets the structured payload as a JSON document.</summary>
    public string PayloadJson { get; set; } = "{}";
}
