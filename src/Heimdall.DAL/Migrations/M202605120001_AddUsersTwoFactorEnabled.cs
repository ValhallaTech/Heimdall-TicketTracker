using FluentMigrator;

namespace Heimdall.DAL.Migrations;

/// <summary>
/// Adds the <c>two_factor_enabled</c> column to the <c>users</c> table (Phase 4.1 step 1
/// of <c>docs/proposals/security-and-authorization.md</c> §9.3). The existing
/// <c>security_stamp</c> column already rotates on credential changes through Identity,
/// so no additional stamp work is required here. pgTAP coverage for the column is added
/// separately by the Database Expert.
/// </summary>
[Migration(202605120001, "Add users.two_factor_enabled")]
public class M202605120001_AddUsersTwoFactorEnabled : Migration
{
    /// <summary>
    /// Adds <c>two_factor_enabled boolean NOT NULL DEFAULT false</c> to the
    /// <c>users</c> table. The default lets the column be added in-place without a
    /// data-backfill step.
    /// </summary>
    public override void Up()
    {
        Execute.Sql(
            "ALTER TABLE users ADD COLUMN two_factor_enabled boolean NOT NULL DEFAULT false;"
        );
    }

    /// <summary>
    /// Drops the <c>two_factor_enabled</c> column from the <c>users</c> table.
    /// </summary>
    public override void Down()
    {
        Execute.Sql("ALTER TABLE users DROP COLUMN two_factor_enabled;");
    }
}
