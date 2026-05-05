-- 05_teams.sql
-- pgTAP coverage for the teams table created by
-- src/Heimdall.DAL/Migrations/M202605050011_CreateTeams.cs
-- (Phase 2.1 step 2 of docs/proposals/team-collaboration.md §4).

BEGIN;

SELECT plan(28);

-- ---------------------------------------------------------------------------
-- Table & columns exist
-- ---------------------------------------------------------------------------
SELECT has_table('teams');

SELECT has_column('teams', 'id');
SELECT has_column('teams', 'organization_id');
SELECT has_column('teams', 'slug');
SELECT has_column('teams', 'name');
SELECT has_column('teams', 'created_at');
SELECT has_column('teams', 'created_by');

-- ---------------------------------------------------------------------------
-- Column types
-- ---------------------------------------------------------------------------
SELECT col_type_is('teams', 'id', 'uuid');
SELECT col_type_is('teams', 'organization_id', 'uuid');
SELECT col_type_is('teams', 'slug', 'citext');
SELECT col_type_is('teams', 'name', 'text');
SELECT col_type_is('teams', 'created_at', 'timestamp with time zone');
SELECT col_type_is('teams', 'created_by', 'uuid');

-- ---------------------------------------------------------------------------
-- Nullability — every column is NOT NULL.
-- ---------------------------------------------------------------------------
SELECT col_not_null('teams', 'id');
SELECT col_not_null('teams', 'organization_id');
SELECT col_not_null('teams', 'slug');
SELECT col_not_null('teams', 'name');
SELECT col_not_null('teams', 'created_at');
SELECT col_not_null('teams', 'created_by');

-- ---------------------------------------------------------------------------
-- Defaults — created_at carries the column-level DEFAULT now() so callers can
-- omit it from INSERTs (matches OrganizationRepository / TeamRepository
-- INSERT SQL which leaves created_at unset).
-- ---------------------------------------------------------------------------
SELECT col_has_default('teams', 'created_at');
SELECT col_default_is('teams', 'created_at', 'now()');

-- ---------------------------------------------------------------------------
-- Primary key + composite unique + supporting indexes
-- ---------------------------------------------------------------------------
SELECT col_is_pk('teams', 'id');
SELECT index_is_unique('ux_teams_organization_id_slug');
SELECT has_index('teams', 'ix_teams_created_by');

-- ---------------------------------------------------------------------------
-- Cascade rule: deleting an organization drops its teams.
-- ---------------------------------------------------------------------------
INSERT INTO users (email, normalized_email, security_stamp, concurrency_stamp, created_at, updated_at)
VALUES ('owner@example.com', 'OWNER@EXAMPLE.COM', 's', 'c', now(), now());

INSERT INTO organizations (slug, name, created_by)
SELECT 'parent-a', 'Parent A', id FROM users WHERE email = 'owner@example.com';

INSERT INTO organizations (slug, name, created_by)
SELECT 'parent-b', 'Parent B', id FROM users WHERE email = 'owner@example.com';

INSERT INTO teams (organization_id, slug, name, created_by)
SELECT o.id, 'team-1', 'Team 1', u.id
FROM organizations o, users u
WHERE o.slug = 'parent-a' AND u.email = 'owner@example.com';

INSERT INTO teams (organization_id, slug, name, created_by)
SELECT o.id, 'team-2', 'Team 2', u.id
FROM organizations o, users u
WHERE o.slug = 'parent-b' AND u.email = 'owner@example.com';

-- Same slug "platform" can exist under two different orgs.
INSERT INTO teams (organization_id, slug, name, created_by)
SELECT o.id, 'platform', 'Platform A', u.id
FROM organizations o, users u
WHERE o.slug = 'parent-a' AND u.email = 'owner@example.com';

INSERT INTO teams (organization_id, slug, name, created_by)
SELECT o.id, 'platform', 'Platform B', u.id
FROM organizations o, users u
WHERE o.slug = 'parent-b' AND u.email = 'owner@example.com';

SELECT is(
    (SELECT COUNT(*)::int FROM teams WHERE slug = 'platform'),
    2,
    'same slug allowed under two different organizations'
);

-- Duplicate within a single org collides on the composite unique.
SAVEPOINT dup_within_org;
SELECT throws_ok(
    $$
    INSERT INTO teams (organization_id, slug, name, created_by)
    SELECT o.id, 'team-1', 'Team 1 (dup)', u.id
    FROM organizations o, users u
    WHERE o.slug = 'parent-a' AND u.email = 'owner@example.com'
    $$,
    '23505',
    NULL,
    'duplicate slug within the same organization violates composite unique'
);
ROLLBACK TO SAVEPOINT dup_within_org;

-- Deleting parent-a cascades its teams (team-1 + platform).
DELETE FROM organizations WHERE slug = 'parent-a';

SELECT is(
    (SELECT COUNT(*)::int FROM teams WHERE slug = 'team-1'),
    0,
    'team-1 cascade-deleted with parent-a'
);

SELECT is(
    (SELECT COUNT(*)::int FROM teams WHERE slug = 'platform'),
    1,
    'only parent-b''s platform team remains after parent-a cascade'
);

SELECT * FROM finish();

ROLLBACK;
