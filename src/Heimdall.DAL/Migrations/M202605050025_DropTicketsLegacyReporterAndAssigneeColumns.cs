using FluentMigrator;

namespace Heimdall.DAL.Migrations;

/// <summary>
/// Drops the legacy free-form <c>tickets.reporter</c> and <c>tickets.assignee</c>
/// varchar columns for Phase 2.5 step 15 of
/// <c>docs/proposals/team-collaboration.md</c> §4. By this point every ticket has a
/// real <c>reporter_id</c> (NOT NULL after step 14) and an optional
/// <c>assignee_id</c>, making the legacy text columns dead weight.
/// <c>Down()</c> restores the columns as nullable text — the original string data
/// is unrecoverable, but restoring the schema shape keeps a rollback to step 14
/// operationally clean.
/// </summary>
[Migration(202605050025, "Drop tickets legacy reporter and assignee columns")]
public class M202605050025_DropTicketsLegacyReporterAndAssigneeColumns : Migration
{
    /// <summary>
    /// Drops <c>tickets.reporter</c> and <c>tickets.assignee</c>.
    /// </summary>
    public override void Up()
    {
        Execute.Sql("ALTER TABLE tickets DROP COLUMN reporter;");
        Execute.Sql("ALTER TABLE tickets DROP COLUMN assignee;");
    }

    /// <summary>
    /// Best-effort schema-only restoration: re-creates the columns as nullable text.
    /// The original <c>reporter</c> column was <c>varchar(100) NOT NULL</c>; we
    /// re-add it nullable because the original values are gone and a NOT NULL flip
    /// would fail on existing rows. Operators rolling back must repopulate the
    /// columns out-of-band before re-applying any NOT NULL constraint.
    /// </summary>
    public override void Down()
    {
        Execute.Sql("ALTER TABLE tickets ADD COLUMN reporter varchar(100) NULL;");
        Execute.Sql("ALTER TABLE tickets ADD COLUMN assignee varchar(100) NULL;");
    }
}
