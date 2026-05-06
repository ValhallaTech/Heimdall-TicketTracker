using FluentMigrator;

namespace Heimdall.DAL.Migrations;

/// <summary>
/// Tightens <c>tickets.reporter_id</c> to <c>NOT NULL</c> for Phase 2.5 step 14 of
/// <c>docs/proposals/team-collaboration.md</c> §4 and §5.3. Every ticket must have
/// a reporter — this is a core audit invariant. <c>tickets.assignee_id</c>
/// intentionally stays nullable because "unassigned" is a legitimate state for a
/// ticket (newly opened tickets, tickets whose assignee was removed via the
/// <c>ON DELETE SET NULL</c> behavior on the FK). This migration will fail loudly
/// if the runtime <c>TicketDefaultsBackfiller</c> has not populated every legacy
/// row's <c>reporter_id</c> first.
/// </summary>
[Migration(202605050024, "Alter tickets.reporter_id NOT NULL")]
public class M202605050024_AlterTicketsReporterIdNotNull : Migration
{
    /// <summary>
    /// Sets <c>tickets.reporter_id</c> to <c>NOT NULL</c>.
    /// </summary>
    public override void Up()
    {
        Execute.Sql("ALTER TABLE tickets ALTER COLUMN reporter_id SET NOT NULL;");
    }

    /// <summary>
    /// Reverts <c>tickets.reporter_id</c> to nullable.
    /// </summary>
    public override void Down()
    {
        Execute.Sql("ALTER TABLE tickets ALTER COLUMN reporter_id DROP NOT NULL;");
    }
}
