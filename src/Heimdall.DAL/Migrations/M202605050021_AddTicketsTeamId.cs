using FluentMigrator;

namespace Heimdall.DAL.Migrations;

/// <summary>
/// Adds <c>tickets.team_id</c> for Phase 2.4 step 11 of
/// <c>docs/proposals/team-collaboration.md</c> §4 and §5.1. The team-queue page filters
/// <c>WHERE team_id = :team_id</c>, so the column gets a dedicated B-tree index.
/// Like step 10, the column is added nullable; the runtime
/// <c>TicketDefaultsBackfiller</c> populates it from the bootstrap-created default
/// team because that row does not exist at migrate time. The matching NOT NULL flip
/// (step 12) only succeeds after the runtime backfill has run, which is the desired
/// safety property. <c>ON DELETE RESTRICT</c> preserves ticket history.
/// </summary>
[Migration(202605050021, "Add tickets.team_id")]
public class M202605050021_AddTicketsTeamId : Migration
{
    /// <summary>
    /// Adds <c>tickets.team_id uuid NULL</c> with an FK to <c>teams(id)</c>
    /// (<c>ON DELETE RESTRICT</c>) and the supporting <c>ix_tickets_team_id</c>
    /// B-tree index for the per-team queue query.
    /// </summary>
    public override void Up()
    {
        Execute.Sql(
            "ALTER TABLE tickets "
                + "ADD COLUMN team_id uuid NULL "
                + "REFERENCES teams(id) ON DELETE RESTRICT;"
        );

        Execute.Sql("CREATE INDEX ix_tickets_team_id ON tickets (team_id);");
    }

    /// <summary>
    /// Drops the index then the column (the column drop would cascade-drop the index,
    /// but explicit teardown matches the rest of the codebase's reversibility style).
    /// </summary>
    public override void Down()
    {
        Execute.Sql("DROP INDEX IF EXISTS ix_tickets_team_id;");
        Execute.Sql("ALTER TABLE tickets DROP COLUMN team_id;");
    }
}
