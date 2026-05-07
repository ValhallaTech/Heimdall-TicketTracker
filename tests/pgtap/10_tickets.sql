-- 10_tickets.sql
-- pgTAP coverage for the tickets table after the Phase 2.4 / 2.5 ticket-FK
-- migrations created by:
--   * src/Heimdall.DAL/Migrations/M202605050020_AddTicketsProjectId.cs
--   * src/Heimdall.DAL/Migrations/M202605050021_AddTicketsTeamId.cs
--   * src/Heimdall.DAL/Migrations/M202605050022_AlterTicketsProjectIdAndTeamIdNotNull.cs
--   * src/Heimdall.DAL/Migrations/M202605050023_AddTicketsReporterIdAndAssigneeId.cs
--   * src/Heimdall.DAL/Migrations/M202605050024_AlterTicketsReporterIdNotNull.cs
--   * src/Heimdall.DAL/Migrations/M202605050025_DropTicketsLegacyReporterAndAssigneeColumns.cs
-- (Phase 2.10 step 27 of docs/implementation/phase-2-checklist.md and §4 of
-- docs/proposals/team-collaboration.md).
--
-- Asserts:
--   * tickets.project_id, team_id, reporter_id, assignee_id all exist as uuid
--   * project_id, team_id, reporter_id are NOT NULL; assignee_id is NULL
--     (the "unassigned" ticket state is intentional — see migration 023's
--     XML doc and migration 024's rationale)
--   * the legacy free-form reporter / assignee varchar columns are gone
--     (migration 025)
--   * each FK points at the documented parent: project_id → projects(id),
--     team_id → teams(id), reporter_id → users(id), assignee_id → users(id)
--   * supporting B-tree indexes ix_tickets_team_id, ix_tickets_reporter_id,
--     ix_tickets_assignee_id are present
--   * ON DELETE behaviour exercised end-to-end via real DELETEs:
--       - deleting a project that owns a ticket is blocked by RESTRICT
--       - deleting a team   that owns a ticket is blocked by RESTRICT
--       - deleting a user   that reports a ticket is blocked by RESTRICT
--       - deleting a user   that is only an assignee succeeds and the
--         ticket's assignee_id flips to NULL (ON DELETE SET NULL)
--   * the organizations → teams → projects → *_members cascade chain works
--     end-to-end: deleting an organization wipes its teams, projects, and
--     all three membership tables in a single DELETE
--
-- The team_member_role enum value list / declared order is already covered
-- by tests/pgtap/08_team_members.sql:82-89 — not duplicated here.
--
-- pgTAP RESTRICT note: PostgreSQL raises SQLSTATE 23001 (restrict_violation)
-- for an explicit ON DELETE RESTRICT FK, distinct from 23503
-- (foreign_key_violation) which fires on NO ACTION / INSERT-side violations.
-- The sibling RESTRICT assertions in 07/08/09 use 23001; this file matches.
--
-- Wrapped in BEGIN ... ROLLBACK so it leaves no residue.

BEGIN;

SELECT plan(32);

-- ---------------------------------------------------------------------------
-- Table & FK columns exist
-- ---------------------------------------------------------------------------
SELECT has_table('tickets');

SELECT has_column('tickets', 'project_id');
SELECT has_column('tickets', 'team_id');
SELECT has_column('tickets', 'reporter_id');
SELECT has_column('tickets', 'assignee_id');

-- ---------------------------------------------------------------------------
-- Column types — every FK column is uuid.
-- ---------------------------------------------------------------------------
SELECT col_type_is('tickets', 'project_id', 'uuid');
SELECT col_type_is('tickets', 'team_id', 'uuid');
SELECT col_type_is('tickets', 'reporter_id', 'uuid');
SELECT col_type_is('tickets', 'assignee_id', 'uuid');

-- ---------------------------------------------------------------------------
-- Nullability — assignee_id stays nullable on purpose; everything else is
-- a hard invariant after migrations 022 and 024.
-- ---------------------------------------------------------------------------
SELECT col_not_null('tickets', 'project_id');
SELECT col_not_null('tickets', 'team_id');
SELECT col_not_null('tickets', 'reporter_id');
SELECT col_is_null ('tickets', 'assignee_id');

-- ---------------------------------------------------------------------------
-- Legacy free-form columns are gone (migration 025).
-- ---------------------------------------------------------------------------
SELECT hasnt_column('tickets', 'reporter');
SELECT hasnt_column('tickets', 'assignee');

-- ---------------------------------------------------------------------------
-- Foreign-key targets — project_id and team_id resolve to the hierarchy
-- tables; reporter_id and assignee_id both resolve to users.
-- ---------------------------------------------------------------------------
SELECT fk_ok('tickets', 'project_id',  'projects', 'id');
SELECT fk_ok('tickets', 'team_id',     'teams',    'id');
SELECT fk_ok('tickets', 'reporter_id', 'users',    'id');
SELECT fk_ok('tickets', 'assignee_id', 'users',    'id');

-- ---------------------------------------------------------------------------
-- Supporting indexes (migrations 021 and 023).
-- ---------------------------------------------------------------------------
SELECT has_index('tickets', 'ix_tickets_team_id');
SELECT has_index('tickets', 'ix_tickets_reporter_id');
SELECT has_index('tickets', 'ix_tickets_assignee_id');

-- ===========================================================================
-- Scenario A — ticket FK on-delete behaviour.
--
-- Build a self-contained hierarchy: alice owns the org/team/project and is
-- the ticket's reporter; bob is the ticket's assignee. The RESTRICT FKs on
-- project_id, team_id, and reporter_id should each block a parent DELETE,
-- while the SET NULL FK on assignee_id should let bob be deleted and flip
-- the ticket's assignee_id to NULL.
-- ===========================================================================

INSERT INTO users (email, normalized_email, security_stamp, concurrency_stamp, created_at, updated_at)
VALUES ('alice@example.com', 'ALICE@EXAMPLE.COM', 's', 'c', now(), now());

INSERT INTO users (email, normalized_email, security_stamp, concurrency_stamp, created_at, updated_at)
VALUES ('bob@example.com', 'BOB@EXAMPLE.COM', 's', 'c', now(), now());

INSERT INTO organizations (slug, name, created_by)
SELECT 'org-tix', 'Org Tix', id FROM users WHERE email = 'alice@example.com';

INSERT INTO teams (organization_id, slug, name, created_by)
SELECT o.id, 'team-tix', 'Team Tix', u.id
FROM organizations o, users u
WHERE o.slug = 'org-tix' AND u.email = 'alice@example.com';

INSERT INTO projects (team_id, slug, name, created_by)
SELECT t.id, 'proj-tix', 'Proj Tix', u.id
FROM teams t, users u
WHERE t.slug = 'team-tix' AND u.email = 'alice@example.com';

INSERT INTO tickets (title, description, status, priority, reporter_id, assignee_id, project_id, team_id, date_created, date_updated)
SELECT
    'Ticket A',
    'fk-behaviour ticket',
    0::smallint,
    1::smallint,
    alice.id,
    bob.id,
    p.id,
    t.id,
    now(),
    now()
FROM users alice, users bob, projects p, teams t
WHERE alice.email = 'alice@example.com'
  AND bob.email   = 'bob@example.com'
  AND p.slug      = 'proj-tix'
  AND t.slug      = 'team-tix';

-- RESTRICT — deleting the ticket's project must be blocked.
SAVEPOINT restrict_project;
SELECT throws_ok(
    $$DELETE FROM projects WHERE slug = 'proj-tix'$$,
    '23001',
    NULL,
    'deleting a project that owns a ticket is blocked by project_id RESTRICT FK (SQLSTATE 23001)'
);
ROLLBACK TO SAVEPOINT restrict_project;

-- RESTRICT — deleting the ticket's team must be blocked. The team has no
-- projects rows pointing at it that would block first because we only
-- attempt the team delete here; projects(team_id) CASCADEs from team, but
-- the cascade fans out to projects → tickets(project_id) RESTRICT, which
-- is also a 23001. Either way, the per-ticket invariant holds: a team
-- with live tickets cannot be deleted.
SAVEPOINT restrict_team;
SELECT throws_ok(
    $$DELETE FROM teams WHERE slug = 'team-tix'$$,
    '23001',
    NULL,
    'deleting a team that owns a ticket is blocked by RESTRICT (SQLSTATE 23001)'
);
ROLLBACK TO SAVEPOINT restrict_team;

-- RESTRICT — deleting the ticket's reporter must be blocked.
SAVEPOINT restrict_reporter;
SELECT throws_ok(
    $$DELETE FROM users WHERE email = 'alice@example.com'$$,
    '23001',
    NULL,
    'deleting a user that reports a ticket is blocked by reporter_id RESTRICT FK (SQLSTATE 23001)'
);
ROLLBACK TO SAVEPOINT restrict_reporter;

-- SET NULL — deleting a user that is *only* an assignee must succeed and
-- flip the ticket's assignee_id to NULL.
SELECT lives_ok(
    $$DELETE FROM users WHERE email = 'bob@example.com'$$,
    'deleting an assignee-only user succeeds (assignee_id ON DELETE SET NULL)'
);

SELECT is(
    (SELECT assignee_id FROM tickets WHERE title = 'Ticket A'),
    NULL::uuid,
    'ticket.assignee_id is NULLed when the assignee user is deleted'
);

-- ===========================================================================
-- Scenario B — organizations → teams → projects → *_members cascade chain.
--
-- Build a *separate* hierarchy with no tickets attached, populate every
-- membership table, then delete the organization. Every descendant row
-- must be gone in a single DELETE.
-- ===========================================================================

INSERT INTO users (email, normalized_email, security_stamp, concurrency_stamp, created_at, updated_at)
VALUES ('dave@example.com', 'DAVE@EXAMPLE.COM', 's', 'c', now(), now());

INSERT INTO organizations (slug, name, created_by)
SELECT 'org-cas', 'Org Cas', id FROM users WHERE email = 'dave@example.com';

INSERT INTO teams (organization_id, slug, name, created_by)
SELECT o.id, 'team-cas', 'Team Cas', u.id
FROM organizations o, users u
WHERE o.slug = 'org-cas' AND u.email = 'dave@example.com';

INSERT INTO projects (team_id, slug, name, created_by)
SELECT t.id, 'proj-cas', 'Proj Cas', u.id
FROM teams t, users u
WHERE t.slug = 'team-cas' AND u.email = 'dave@example.com';

INSERT INTO organization_members (user_id, organization_id, role, added_by)
SELECT u.id, o.id, 'owner', u.id
FROM users u, organizations o
WHERE u.email = 'dave@example.com' AND o.slug = 'org-cas';

INSERT INTO team_members (user_id, team_id, role, added_by)
SELECT u.id, t.id, 'manager', u.id
FROM users u, teams t
WHERE u.email = 'dave@example.com' AND t.slug = 'team-cas';

INSERT INTO project_members (user_id, project_id, role, added_by)
SELECT u.id, p.id, 'owner', u.id
FROM users u, projects p
WHERE u.email = 'dave@example.com' AND p.slug = 'proj-cas';

-- One DELETE on the org should fan out through teams, projects, and all
-- three membership tables.
DELETE FROM organizations WHERE slug = 'org-cas';

SELECT is(
    (SELECT COUNT(*)::int FROM teams WHERE slug = 'team-cas'),
    0,
    'deleting an organization cascades to its teams'
);

SELECT is(
    (SELECT COUNT(*)::int FROM projects WHERE slug = 'proj-cas'),
    0,
    'deleting an organization cascades through teams to its projects'
);

SELECT is(
    (SELECT COUNT(*)::int FROM organization_members om
      JOIN users u ON u.id = om.user_id
      WHERE u.email = 'dave@example.com'),
    0,
    'deleting an organization cascades to organization_members'
);

SELECT is(
    (SELECT COUNT(*)::int FROM team_members tm
      JOIN users u ON u.id = tm.user_id
      WHERE u.email = 'dave@example.com'),
    0,
    'deleting an organization cascades through teams to team_members'
);

SELECT is(
    (SELECT COUNT(*)::int FROM project_members pm
      JOIN users u ON u.id = pm.user_id
      WHERE u.email = 'dave@example.com'),
    0,
    'deleting an organization cascades through teams → projects to project_members'
);

SELECT * FROM finish();

ROLLBACK;
