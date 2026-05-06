using FluentMigrator;

namespace Heimdall.DAL.Migrations;

/// <summary>
/// Creates the <c>organization_members</c> table and its companion
/// <c>org_member_role</c> enum for Phase 2.2 step 4 of
/// <c>docs/proposals/team-collaboration.md</c> §3.1 + §4. The role enum
/// (<c>owner</c>, <c>admin</c>, <c>member</c>, <c>viewer</c>) is the canonical wire
/// representation consumed by OpenFGA in Phase 3. Foreign keys split intentionally:
/// <c>user_id</c> and <c>organization_id</c> use <c>ON DELETE CASCADE</c> so removing
/// either parent tears down its membership rows, while <c>added_by</c> uses
/// <c>ON DELETE RESTRICT</c> to preserve invitation provenance — the same
/// data-preservation rationale that drives <c>created_by</c> RESTRICT on the
/// <c>organizations</c>, <c>teams</c>, and <c>projects</c> hierarchy tables.
/// </summary>
[Migration(202605050013, "Create organization_members")]
public class M202605050013_CreateOrganizationMembers : Migration
{
    /// <summary>
    /// Creates the <c>org_member_role</c> enum and the <c>organization_members</c>
    /// table. <c>user_id</c> and <c>organization_id</c> CASCADE; <c>added_by</c>
    /// RESTRICTs to preserve the audit trail of who issued the invitation.
    /// </summary>
    public override void Up()
    {
        Execute.Sql("CREATE TYPE org_member_role AS ENUM ('owner', 'admin', 'member', 'viewer');");

        Execute.Sql(@"
CREATE TABLE organization_members (
    user_id         uuid             NOT NULL REFERENCES users(id)         ON DELETE CASCADE,
    organization_id uuid             NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    role            org_member_role  NOT NULL,
    added_at        timestamptz      NOT NULL DEFAULT now(),
    added_by        uuid             NOT NULL REFERENCES users(id)         ON DELETE RESTRICT,
    PRIMARY KEY (user_id, organization_id)
);");

        Execute.Sql(
            "CREATE INDEX ix_organization_members_organization_id "
                + "ON organization_members (organization_id);"
        );

        Execute.Sql(
            "CREATE INDEX ix_organization_members_added_by "
                + "ON organization_members (added_by);"
        );
    }

    /// <summary>
    /// Drops the <c>organization_members</c> table first, then the
    /// <c>org_member_role</c> enum (the enum cannot be dropped while a column still
    /// references it).
    /// </summary>
    public override void Down()
    {
        Delete.Table("organization_members");
        Execute.Sql("DROP TYPE org_member_role;");
    }
}
