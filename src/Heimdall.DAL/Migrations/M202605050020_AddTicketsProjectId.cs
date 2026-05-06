using FluentMigrator;

namespace Heimdall.DAL.Migrations;

/// <summary>
/// Adds <c>tickets.project_id</c> for Phase 2.4 step 10 of
/// <c>docs/proposals/team-collaboration.md</c> §4. The column is added <em>nullable</em>
/// because the default project row that existing tickets must reference is created at
/// runtime by <c>DefaultHierarchyBootstrapper</c> (introduced in Phase 2.3 step 9), not
/// by a migration. A subsequent runtime <c>TicketDefaultsBackfiller</c> populates this
/// column for legacy rows; only after that has run does the matching NOT NULL flip
/// migration (step 12) tighten the schema. Doing the backfill in SQL here would either
/// require duplicating the bootstrapper's tenancy logic or hard-coding a UUID, neither
/// of which is acceptable. The FK uses <c>ON DELETE RESTRICT</c> to preserve ticket
/// history if someone attempts to delete a project that still owns tickets.
/// </summary>
[Migration(202605050020, "Add tickets.project_id")]
public class M202605050020_AddTicketsProjectId : Migration
{
    /// <summary>
    /// Adds <c>tickets.project_id uuid NULL</c> with an FK to <c>projects(id)</c>
    /// (<c>ON DELETE RESTRICT</c>). No backfill — that is the runtime
    /// <c>TicketDefaultsBackfiller</c>'s job.
    /// </summary>
    public override void Up()
    {
        Execute.Sql(
            "ALTER TABLE tickets "
                + "ADD COLUMN project_id uuid NULL "
                + "REFERENCES projects(id) ON DELETE RESTRICT;"
        );
    }

    /// <summary>
    /// Drops <c>tickets.project_id</c>. The FK is dropped implicitly with the column.
    /// </summary>
    public override void Down()
    {
        Execute.Sql("ALTER TABLE tickets DROP COLUMN project_id;");
    }
}
