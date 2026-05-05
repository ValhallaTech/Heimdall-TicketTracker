using System;

namespace Heimdall.Core.Auditing;

/// <summary>
/// Append-only audit-event record. Mirrors the <c>audit_events</c> table created by
/// <c>M202605050002_CreateAuditEvents</c>. The <c>occurred_at</c> column is intentionally
/// not modelled here — it is sourced from the database clock (<c>now()</c>) to avoid
/// client-clock skew, matching the pattern used by <c>HeimdallUserStore</c>.
/// </summary>
public class AuditEvent
{
    /// <summary>
    /// Gets or sets the identifier of the user who triggered the event, or <c>null</c>
    /// for anonymous / pre-authentication events (e.g. failed logins for unknown users).
    /// </summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>
    /// Gets or sets the dotted event name (e.g. <c>"login.success"</c>,
    /// <c>"login.failure.bad_password"</c>). Plain <c>text</c> at the column level so a new
    /// event type can be added without a schema migration.
    /// </summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional target identifier the event acted on (typically a user
    /// id rendered as text). Null when the event is not bound to a specific target.
    /// </summary>
    public string? Target { get; set; }

    /// <summary>
    /// Gets or sets the client IP address recorded at event time, or <c>null</c> when
    /// unavailable. Stored as Postgres <c>inet</c>; expected to be a parseable v4/v6 literal.
    /// </summary>
    public string? Ip { get; set; }

    /// <summary>
    /// Gets or sets the client User-Agent string, or <c>null</c> when not present. Callers
    /// are expected to truncate to a sensible upper bound (~512 chars) before assignment.
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// Gets or sets the structured payload as a JSON document. Defaults to <c>"{}"</c>.
    /// Persisted into a Postgres <c>jsonb</c> column — must be valid JSON.
    /// </summary>
    public string PayloadJson { get; set; } = "{}";
}
