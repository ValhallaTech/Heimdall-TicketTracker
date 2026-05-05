using FluentMigrator;

namespace Heimdall.DAL.Migrations;

/// <summary>
/// Creates the <c>organizations</c> table for Phase 2.1 step 1 of
/// <c>docs/proposals/team-collaboration.md</c> §4. Organizations are the top of the
/// object hierarchy that ReBAC will eventually authorize against (orgs → teams →
/// projects → tickets). UUID PK with <c>gen_random_uuid()</c> default mirrors the
/// <c>users</c> table; <c>citext</c> slug is unique globally (organizations are the
/// top-level scope, so there is no parent to scope uniqueness against). The
/// <c>created_by</c> FK uses <c>ON DELETE RESTRICT</c> so deleting a user that
/// authored organizations fails fast — the proposal calls out a separate
/// "transfer ownership" flow rather than cascading data loss.
/// </summary>
[Migration(202605050010, "Create organizations")]
public class M202605050010_CreateOrganizations : Migration
{
    /// <summary>
    /// Creates the <c>organizations</c> table. The <c>pgcrypto</c> and <c>citext</c>
    /// extensions are already installed by <c>M202605050001_CreateUsers</c>, so they
    /// are not re-declared here. Following the precedent set by the users / audit
    /// migrations, the full <c>CREATE TABLE</c> is authored in raw SQL because
    /// FluentMigrator's fluent column API does not natively understand <c>citext</c>
    /// or the <c>gen_random_uuid()</c> default expression.
    /// </summary>
    public override void Up()
    {
        Execute.Sql(@"
CREATE TABLE organizations (
    id          uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    slug        citext      NOT NULL,
    name        text        NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    created_by  uuid        NOT NULL REFERENCES users(id) ON DELETE RESTRICT
);");

        // Dedicated, named unique index (rather than an inline UNIQUE constraint) so
        // pgTAP can assert against a stable index name and so it can be dropped /
        // recreated independently if the lookup pattern changes. The unique index
        // doubles as the supporting B-tree for slug lookups — no separate index needed.
        Execute.Sql("CREATE UNIQUE INDEX ux_organizations_slug ON organizations (slug);");

        // Supports "list all organizations created by user X" queries from the admin
        // panel. Without this, the admin "transfer ownership" flow would do a full
        // table scan to find the source user's organizations.
        Execute.Sql("CREATE INDEX ix_organizations_created_by ON organizations (created_by);");
    }

    /// <summary>
    /// Drops the <c>organizations</c> table. The <c>pgcrypto</c> and <c>citext</c>
    /// extensions are intentionally left in place — they are owned by the earlier
    /// users migration.
    /// </summary>
    public override void Down()
    {
        Delete.Table("organizations");
    }
}
