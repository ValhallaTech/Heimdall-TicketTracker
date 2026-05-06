using FluentMigrator;

namespace Heimdall.DAL.Migrations;

/// <summary>
/// Adds <c>tickets.reporter_id</c> and <c>tickets.assignee_id</c> for Phase 2.5
/// step 13 of <c>docs/proposals/team-collaboration.md</c> §4 and §5.3. These replace
/// the legacy free-form <c>reporter</c> and <c>assignee</c> varchar columns, which
/// will be dropped in step 15. Both columns are added nullable; the runtime
/// <c>TicketDefaultsBackfiller</c> populates <c>reporter_id</c> from the bootstrap
/// admin and leaves <c>assignee_id</c> NULL when the legacy seed string cannot be
/// resolved to a real user. Step 14 then tightens <c>reporter_id</c> to NOT NULL —
/// <c>assignee_id</c> stays nullable because "unassigned" is a real ticket state.
/// FK semantics differ on purpose: <c>reporter_id</c> uses <c>ON DELETE RESTRICT</c>
/// (reporter is part of the ticket's audit identity and must not be silently lost),
/// while <c>assignee_id</c> uses <c>ON DELETE SET NULL</c> (when an assignee is
/// removed, the ticket falls back to unassigned rather than blocking the user
/// deletion). Both columns are indexed because the assign / route flows filter on
/// them.
/// </summary>
[Migration(202605050023, "Add tickets.reporter_id and tickets.assignee_id")]
public class M202605050023_AddTicketsReporterIdAndAssigneeId : Migration
{
    /// <summary>
    /// Adds the two UUID FK columns and their supporting B-tree indexes.
    /// </summary>
    public override void Up()
    {
        Execute.Sql(
            "ALTER TABLE tickets "
                + "ADD COLUMN reporter_id uuid NULL "
                + "REFERENCES users(id) ON DELETE RESTRICT;"
        );
        Execute.Sql(
            "ALTER TABLE tickets "
                + "ADD COLUMN assignee_id uuid NULL "
                + "REFERENCES users(id) ON DELETE SET NULL;"
        );

        Execute.Sql("CREATE INDEX ix_tickets_reporter_id ON tickets (reporter_id);");
        Execute.Sql("CREATE INDEX ix_tickets_assignee_id ON tickets (assignee_id);");
    }

    /// <summary>
    /// Drops the indexes and columns.
    /// </summary>
    public override void Down()
    {
        Execute.Sql("DROP INDEX IF EXISTS ix_tickets_assignee_id;");
        Execute.Sql("DROP INDEX IF EXISTS ix_tickets_reporter_id;");
        Execute.Sql("ALTER TABLE tickets DROP COLUMN assignee_id;");
        Execute.Sql("ALTER TABLE tickets DROP COLUMN reporter_id;");
    }
}
