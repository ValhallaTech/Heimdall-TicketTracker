-- 06_projects.sql
-- pgTAP coverage for the projects table created by
-- src/Heimdall.DAL/Migrations/M202605050012_CreateProjects.cs
-- (Phase 2.1 step 3 of docs/proposals/team-collaboration.md §4).

BEGIN;

SELECT plan(20);

-- ---------------------------------------------------------------------------
-- Table & columns exist
-- ---------------------------------------------------------------------------
SELECT has_table('projects');

SELECT has_column('projects', 'id');
SELECT has_column('projects', 'team_id');
SELECT has_column('projects', 'slug');
SELECT has_column('projects', 'name');
SELECT has_column('projects', 'created_at');
SELECT has_column('projects', 'created_by');

-- ---------------------------------------------------------------------------
-- Column types
-- ---------------------------------------------------------------------------
SELECT col_type_is('projects', 'id', 'uuid');
SELECT col_type_is('projects', 'team_id', 'uuid');
SELECT col_type_is('projects', 'slug', 'citext');
SELECT col_type_is('projects', 'name', 'text');
SELECT col_type_is('projects', 'created_at', 'timestamp with time zone');
SELECT col_type_is('projects', 'created_by', 'uuid');

-- ---------------------------------------------------------------------------
-- Primary key + composite unique + supporting indexes
-- ---------------------------------------------------------------------------
SELECT col_is_pk('projects', 'id');
SELECT index_is_unique('ux_projects_team_id_slug');
SELECT has_index('projects', 'ix_projects_created_by');

-- ---------------------------------------------------------------------------
-- Cascade rule: deleting a team drops its projects.
-- ---------------------------------------------------------------------------
INSERT INTO users (email, normalized_email, security_stamp, concurrency_stamp, created_at, updated_at)
VALUES ('owner@example.com', 'OWNER@EXAMPLE.COM', 's', 'c', now(), now());

INSERT INTO organizations (slug, name, created_by)
SELECT 'org-1', 'Org', id FROM users WHERE email = 'owner@example.com';

INSERT INTO teams (organization_id, slug, name, created_by)
SELECT o.id, 'alpha', 'Alpha', u.id
FROM organizations o, users u
WHERE o.slug = 'org-1' AND u.email = 'owner@example.com';

INSERT INTO teams (organization_id, slug, name, created_by)
SELECT o.id, 'beta', 'Beta', u.id
FROM organizations o, users u
WHERE o.slug = 'org-1' AND u.email = 'owner@example.com';

-- Same slug "backend" can exist under two different teams.
INSERT INTO projects (team_id, slug, name, created_by)
SELECT t.id, 'backend', 'Alpha Backend', u.id
FROM teams t, users u
WHERE t.slug = 'alpha' AND u.email = 'owner@example.com';

INSERT INTO projects (team_id, slug, name, created_by)
SELECT t.id, 'backend', 'Beta Backend', u.id
FROM teams t, users u
WHERE t.slug = 'beta' AND u.email = 'owner@example.com';

SELECT is(
    (SELECT COUNT(*)::int FROM projects WHERE slug = 'backend'),
    2,
    'same slug allowed under two different teams'
);

SAVEPOINT dup_within_team;
SELECT throws_ok(
    $$
    INSERT INTO projects (team_id, slug, name, created_by)
    SELECT t.id, 'backend', 'Alpha Backend (dup)', u.id
    FROM teams t, users u
    WHERE t.slug = 'alpha' AND u.email = 'owner@example.com'
    $$,
    '23505',
    NULL,
    'duplicate slug within the same team violates composite unique'
);
ROLLBACK TO SAVEPOINT dup_within_team;

-- Deleting team alpha cascades its projects.
DELETE FROM teams WHERE slug = 'alpha';

SELECT is(
    (SELECT COUNT(*)::int FROM projects WHERE name = 'Alpha Backend'),
    0,
    'alpha''s backend project cascade-deleted with team'
);

SELECT is(
    (SELECT COUNT(*)::int FROM projects WHERE name = 'Beta Backend'),
    1,
    'beta''s backend project survives sibling team delete'
);

SELECT * FROM finish();

ROLLBACK;
