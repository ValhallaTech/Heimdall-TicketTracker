using FluentMigrator;

namespace Heimdall.DAL.Migrations;

/// <summary>
/// Creates the <c>project_members</c> table and its companion
/// <c>project_member_role</c> enum for Phase 2.2 step 4 of
/// <c>docs/proposals/team-collaboration.md</c> §3.1 + §4. The role enum
/// (<c>owner</c>, <c>admin</c>, <c>member</c>, <c>viewer</c>) is the canonical wire
/// representation consumed by OpenFGA in Phase 3. Foreign keys split intentionally:
/// <c>user_id</c> and <c>project_id</c> use <c>ON DELETE CASCADE</c> so removing
/// either parent tears down its membership rows, while <c>added_by</c> uses
/// <c>ON DELETE RESTRICT</c> to preserve invitation provenance — same rationale as
/// <c>created_by</c> RESTRICT on the hierarchy tables.
/// </summary>
[Migration(202605050015, "Create project_members")]
public class M202605050015_CreateProjectMembers : Migration
{
    /// <summary>
    /// Creates the <c>project_member_role</c> enum and the <c>project_members</c>
    /// table. <c>user_id</c> and <c>project_id</c> CASCADE; <c>added_by</c>
    /// RESTRICTs to preserve the audit trail of who issued the invitation.
    /// </summary>
    public override void Up()
    {
        Execute.Sql("CREATE TYPE project_member_role AS ENUM ('owner', 'admin', 'member', 'viewer');");

        Execute.Sql(@"
CREATE TABLE project_members (
    user_id    uuid                 NOT NULL REFERENCES users(id)    ON DELETE CASCADE,
    project_id uuid                 NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    role       project_member_role  NOT NULL,
    added_at   timestamptz          NOT NULL DEFAULT now(),
    added_by   uuid                 NOT NULL REFERENCES users(id)    ON DELETE RESTRICT,
    PRIMARY KEY (user_id, project_id)
);");

        Execute.Sql("CREATE INDEX ix_project_members_project_id ON project_members (project_id);");
        Execute.Sql("CREATE INDEX ix_project_members_added_by ON project_members (added_by);");
    }

    /// <summary>
    /// Drops the <c>project_members</c> table first, then the
    /// <c>project_member_role</c> enum (the enum cannot be dropped while a column
    /// still references it).
    /// </summary>
    public override void Down()
    {
        Delete.Table("project_members");
        Execute.Sql("DROP TYPE project_member_role;");
    }
}
