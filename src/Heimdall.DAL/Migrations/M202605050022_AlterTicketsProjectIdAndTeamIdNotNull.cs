using FluentMigrator;

namespace Heimdall.DAL.Migrations;

/// <summary>
/// Tightens <c>tickets.project_id</c> and <c>tickets.team_id</c> to <c>NOT NULL</c>
/// for Phase 2.4 step 12 of <c>docs/proposals/team-collaboration.md</c> §4. Both
/// columns are flipped in a <em>single</em> migration so the invariant — every ticket
/// has a parent project and team — becomes true atomically. This migration will fail
/// loudly if the runtime <c>TicketDefaultsBackfiller</c> has not populated every
/// legacy row first; that's the intended safety net.
/// </summary>
[Migration(202605050022, "Alter tickets.project_id and tickets.team_id NOT NULL")]
public class M202605050022_AlterTicketsProjectIdAndTeamIdNotNull : Migration
{
    /// <summary>
    /// Sets <c>tickets.project_id</c> and <c>tickets.team_id</c> to <c>NOT NULL</c>.
    /// </summary>
    public override void Up()
    {
        Execute.Sql("ALTER TABLE tickets ALTER COLUMN project_id SET NOT NULL;");
        Execute.Sql("ALTER TABLE tickets ALTER COLUMN team_id SET NOT NULL;");
    }

    /// <summary>
    /// Reverts both columns to nullable.
    /// </summary>
    public override void Down()
    {
        Execute.Sql("ALTER TABLE tickets ALTER COLUMN team_id DROP NOT NULL;");
        Execute.Sql("ALTER TABLE tickets ALTER COLUMN project_id DROP NOT NULL;");
    }
}
