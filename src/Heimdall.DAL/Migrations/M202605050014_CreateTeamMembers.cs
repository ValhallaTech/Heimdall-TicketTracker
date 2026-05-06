using FluentMigrator;

namespace Heimdall.DAL.Migrations;

/// <summary>
/// Creates the <c>team_members</c> table and its companion <c>team_member_role</c>
/// enum for Phase 2.2 step 4 of <c>docs/proposals/team-collaboration.md</c> §3.1 +
/// §4. The role enum (<c>manager</c>, <c>team_lead</c>, <c>member</c>,
/// <c>viewer</c>) is the canonical wire representation consumed by
/// <c>IPermissionService</c> in Phase 2.6 and OpenFGA in Phase 3. Foreign keys split
/// intentionally: <c>user_id</c> and <c>team_id</c> use <c>ON DELETE CASCADE</c> so
/// removing either parent tears down its membership rows, while <c>added_by</c>
/// uses <c>ON DELETE RESTRICT</c> to preserve invitation provenance — same
/// rationale as <c>created_by</c> RESTRICT on the hierarchy tables.
/// </summary>
[Migration(202605050014, "Create team_members")]
public class M202605050014_CreateTeamMembers : Migration
{
    /// <summary>
    /// Creates the <c>team_member_role</c> enum and the <c>team_members</c> table.
    /// <c>user_id</c> and <c>team_id</c> CASCADE; <c>added_by</c> RESTRICTs to
    /// preserve the audit trail of who issued the invitation.
    /// </summary>
    public override void Up()
    {
        Execute.Sql("CREATE TYPE team_member_role AS ENUM ('manager', 'team_lead', 'member', 'viewer');");

        Execute.Sql(@"
CREATE TABLE team_members (
    user_id   uuid              NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    team_id   uuid              NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    role      team_member_role  NOT NULL,
    added_at  timestamptz       NOT NULL DEFAULT now(),
    added_by  uuid              NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    PRIMARY KEY (user_id, team_id)
);");

        Execute.Sql("CREATE INDEX ix_team_members_team_id ON team_members (team_id);");
        Execute.Sql("CREATE INDEX ix_team_members_added_by ON team_members (added_by);");
    }

    /// <summary>
    /// Drops the <c>team_members</c> table first, then the <c>team_member_role</c>
    /// enum (the enum cannot be dropped while a column still references it).
    /// </summary>
    public override void Down()
    {
        Delete.Table("team_members");
        Execute.Sql("DROP TYPE team_member_role;");
    }
}
