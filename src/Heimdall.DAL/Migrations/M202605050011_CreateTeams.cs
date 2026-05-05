using FluentMigrator;

namespace Heimdall.DAL.Migrations;

/// <summary>
/// Creates the <c>teams</c> table for Phase 2.1 step 2 of
/// <c>docs/proposals/team-collaboration.md</c> §4. Teams sit one level below
/// organizations in the hierarchy and are scoped per-org: two organizations can both
/// have a team called <c>platform</c>. The composite unique index on
/// <c>(organization_id, slug)</c> enforces this and doubles as the lookup index for
/// "find team by org + slug". A separate index on <c>organization_id</c> supports the
/// parent-only listing query ("show all teams in this org") and is the index the
/// planner uses when cascading the <c>ON DELETE CASCADE</c> from the organization
/// FK — without it, a parent-org delete would issue a full table scan per cascade
/// step.
/// </summary>
[Migration(202605050011, "Create teams")]
public class M202605050011_CreateTeams : Migration
{
    /// <summary>
    /// Creates the <c>teams</c> table. <c>organization_id</c> uses
    /// <c>ON DELETE CASCADE</c> — deleting an organization tears down its teams (and
    /// transitively its projects, members, and tickets) per the hierarchy contract.
    /// <c>created_by</c> uses <c>ON DELETE RESTRICT</c> for the same data-preservation
    /// reason as on <c>organizations</c>.
    /// </summary>
    public override void Up()
    {
        Execute.Sql(@"
CREATE TABLE teams (
    id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id uuid        NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    slug            citext      NOT NULL,
    name            text        NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    created_by      uuid        NOT NULL REFERENCES users(id) ON DELETE RESTRICT
);");

        // Composite unique on (organization_id, slug). The proposal §4 step 2 calls
        // for this, and the proposal also asks for a standalone index on
        // organization_id. The composite already supports WHERE organization_id = $1
        // (organization_id is the leading column), so the standalone index would be
        // strictly redundant — we trust the composite to serve both lookups. This
        // matches the "no dead-weight indexes" stance from M202604300001_CreateTickets.
        Execute.Sql(
            "CREATE UNIQUE INDEX ux_teams_organization_id_slug "
                + "ON teams (organization_id, slug);"
        );

        // Index on created_by to support admin "find teams created by user X" — same
        // rationale as the equivalent index on organizations.
        Execute.Sql("CREATE INDEX ix_teams_created_by ON teams (created_by);");
    }

    /// <inheritdoc />
    public override void Down()
    {
        Delete.Table("teams");
    }
}
