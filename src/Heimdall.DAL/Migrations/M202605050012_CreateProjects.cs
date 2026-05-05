using FluentMigrator;

namespace Heimdall.DAL.Migrations;

/// <summary>
/// Creates the <c>projects</c> table for Phase 2.1 step 3 of
/// <c>docs/proposals/team-collaboration.md</c> §4. Projects are scoped per-team and
/// uniqueness is enforced on <c>(team_id, slug)</c>: two teams can both own a
/// project called <c>backend</c>. Tickets will gain a <c>project_id</c> FK in
/// Phase 2.4, so projects are the leaf object in the org→team→project ladder before
/// tickets attach.
/// </summary>
[Migration(202605050012, "Create projects")]
public class M202605050012_CreateProjects : Migration
{
    /// <summary>
    /// Creates the <c>projects</c> table. <c>team_id</c> uses <c>ON DELETE CASCADE</c>
    /// — deleting a team tears down its projects. <c>created_by</c> uses
    /// <c>ON DELETE RESTRICT</c> for data-preservation parity with the
    /// <c>organizations</c> and <c>teams</c> tables.
    /// </summary>
    public override void Up()
    {
        Execute.Sql(@"
CREATE TABLE projects (
    id          uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    team_id     uuid        NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    slug        citext      NOT NULL,
    name        text        NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    created_by  uuid        NOT NULL REFERENCES users(id) ON DELETE RESTRICT
);");

        // Composite unique (team_id, slug); see the equivalent comment on
        // M202605050011_CreateTeams for why we don't add a standalone team_id index.
        Execute.Sql(
            "CREATE UNIQUE INDEX ux_projects_team_id_slug "
                + "ON projects (team_id, slug);"
        );

        Execute.Sql("CREATE INDEX ix_projects_created_by ON projects (created_by);");
    }

    /// <inheritdoc />
    public override void Down()
    {
        Delete.Table("projects");
    }
}
