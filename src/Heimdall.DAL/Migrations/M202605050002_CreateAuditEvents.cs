using FluentMigrator;

namespace Heimdall.DAL.Migrations;

/// <summary>
/// Creates the <c>audit_events</c> table for the Authenticated Foundation (Phase 1 step 2 of
/// <c>docs/proposals/security-and-authorization.md</c> §9.3). The table is intentionally
/// landed before any auth code so every later step can write audit rows from day one.
/// It is append-only and uses PostgreSQL-native <c>inet</c> and <c>jsonb</c> types for the
/// network address and structured payload columns. <c>actor_user_id</c> is nullable and uses
/// <c>ON DELETE SET NULL</c> so anonymous events (e.g. failed logins before user lookup) can
/// be recorded and so deleting a user does not destroy their audit trail. <c>event_type</c>
/// is plain <c>text</c> rather than an enum so adding a new event type does not require a
/// schema migration.
/// </summary>
[Migration(202605050002, "Create audit_events")]
public class M202605050002_CreateAuditEvents : Migration
{
    /// <summary>
    /// Creates the <c>audit_events</c> table and its three supporting indexes. The
    /// <c>pgcrypto</c> extension required for <c>gen_random_uuid()</c> is already installed
    /// by <c>M202605050001_CreateUsers</c> and is therefore not re-declared here. Following
    /// the precedent set by <c>M202605050001_CreateUsers</c> (and the ordered index in
    /// <c>M202604300001_CreateTickets</c>), the full <c>CREATE TABLE</c> and all indexes are
    /// authored in raw SQL: FluentMigrator's fluent column API does not natively understand
    /// <c>inet</c>, <c>jsonb</c>, or the <c>gen_random_uuid()</c> default expression, and its
    /// fluent index API does not reliably emit DESC ordering or partial-index <c>WHERE</c>
    /// clauses for PostgreSQL.
    /// </summary>
    public override void Up()
    {
        Execute.Sql(@"
CREATE TABLE audit_events (
    id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    actor_user_id   uuid        NULL REFERENCES users(id) ON DELETE SET NULL,
    event_type      text        NOT NULL,
    target          text        NULL,
    ip              inet        NULL,
    user_agent      text        NULL,
    payload         jsonb       NOT NULL DEFAULT '{}'::jsonb,
    occurred_at     timestamptz NOT NULL DEFAULT now()
);");

        // Index 1: "What did user X do recently?" — the dominant read pattern. Partial on
        // actor_user_id IS NOT NULL so anonymous events (failed logins, system events) do
        // not bloat the index. DESC on occurred_at matches the natural ORDER BY.
        Execute.Sql(
            "CREATE INDEX ix_audit_events_actor_occurred "
                + "ON audit_events (actor_user_id, occurred_at DESC) "
                + "WHERE actor_user_id IS NOT NULL;"
        );

        // Index 2: "What event_type=X events fired in the last hour?" — supports the
        // alerting / monitoring queries that filter by event_type and time-range together.
        Execute.Sql(
            "CREATE INDEX ix_audit_events_event_type_occurred "
                + "ON audit_events (event_type, occurred_at DESC);"
        );

        // Index 3: bare time-range scans across the entire audit log (e.g. "what happened
        // between T1 and T2 across all actors and event types?").
        Execute.Sql(
            "CREATE INDEX ix_audit_events_occurred_at "
                + "ON audit_events (occurred_at DESC);"
        );
    }

    /// <summary>
    /// Drops the <c>audit_events</c> table. The <c>pgcrypto</c> extension is intentionally
    /// left in place — it is owned by the earlier users migration and may be used by other
    /// objects.
    /// </summary>
    public override void Down()
    {
        Delete.Table("audit_events");
    }
}
