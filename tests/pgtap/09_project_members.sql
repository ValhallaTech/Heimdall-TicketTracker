-- 09_project_members.sql
-- pgTAP coverage for the project_members table and project_member_role enum
-- created by src/Heimdall.DAL/Migrations/M202605050015_CreateProjectMembers.cs
-- (Phase 2.2 step 4 of docs/proposals/team-collaboration.md §3.1 + §4).
--
-- Asserts:
--   * the table exists with all 5 expected columns and correct types / nullability
--   * added_at defaults to now()
--   * (user_id, project_id) is the composite primary key
--   * supporting indexes on project_id and added_by exist
--   * project_member_role enum carries owner/admin/member/viewer in order
--   * deleting the parent project cascades the membership rows
--   * deleting the member user cascades their membership rows
--   * deleting the inviter (added_by) is blocked by the RESTRICT FK (SQLSTATE 23001)
--
-- Wrapped in BEGIN ... ROLLBACK so it leaves no residue.

BEGIN;

SELECT plan(25);

-- ---------------------------------------------------------------------------
-- Table & columns exist
-- ---------------------------------------------------------------------------
SELECT has_table('project_members');

SELECT has_column('project_members', 'user_id');
SELECT has_column('project_members', 'project_id');
SELECT has_column('project_members', 'role');
SELECT has_column('project_members', 'added_at');
SELECT has_column('project_members', 'added_by');

-- ---------------------------------------------------------------------------
-- Column types
-- ---------------------------------------------------------------------------
SELECT col_type_is('project_members', 'user_id', 'uuid');
SELECT col_type_is('project_members', 'project_id', 'uuid');
SELECT col_type_is('project_members', 'role', 'project_member_role');
SELECT col_type_is('project_members', 'added_at', 'timestamp with time zone');
SELECT col_type_is('project_members', 'added_by', 'uuid');

-- ---------------------------------------------------------------------------
-- Nullability — every column is NOT NULL.
-- ---------------------------------------------------------------------------
SELECT col_not_null('project_members', 'user_id');
SELECT col_not_null('project_members', 'project_id');
SELECT col_not_null('project_members', 'role');
SELECT col_not_null('project_members', 'added_at');
SELECT col_not_null('project_members', 'added_by');

-- ---------------------------------------------------------------------------
-- Default — added_at is server-clock (matches ProjectMemberRepository's INSERT,
-- which omits the column from the column list).
-- ---------------------------------------------------------------------------
SELECT col_has_default('project_members', 'added_at');
SELECT col_default_is('project_members', 'added_at', 'now()');

-- ---------------------------------------------------------------------------
-- Composite primary key — version-agnostic pg_constraint check (the existing
-- pgTAP files only use the single-column col_is_pk overload).
-- ---------------------------------------------------------------------------
SELECT is(
    (SELECT array_agg(a.attname ORDER BY a.attnum)
     FROM pg_constraint c
     JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = ANY(c.conkey)
     WHERE c.conrelid = 'project_members'::regclass AND c.contype = 'p'),
    ARRAY['user_id','project_id']::name[],
    'composite primary key is (user_id, project_id)'
);

-- ---------------------------------------------------------------------------
-- Supporting indexes
-- ---------------------------------------------------------------------------
SELECT has_index('project_members', 'ix_project_members_project_id');
SELECT has_index('project_members', 'ix_project_members_added_by');

-- ---------------------------------------------------------------------------
-- Enum value list — values AND declared order are pinned (OpenFGA in Phase 3
-- consumes these wire strings directly).
-- ---------------------------------------------------------------------------
SELECT is(
    (SELECT array_agg(enumlabel::text ORDER BY enumsortorder)
     FROM pg_enum e
     JOIN pg_type t ON e.enumtypid = t.oid
     WHERE t.typname = 'project_member_role'),
    ARRAY['owner','admin','member','viewer']::text[],
    'project_member_role enum values in declared order'
);

-- ---------------------------------------------------------------------------
-- Seed three users (alice as org/team/project owner, bob as member, carol as
-- inviter only) so the restrict-FK assertion below isolates
-- project_members.added_by.
-- ---------------------------------------------------------------------------
INSERT INTO users (email, normalized_email, security_stamp, concurrency_stamp, created_at, updated_at)
VALUES ('alice@example.com', 'ALICE@EXAMPLE.COM', 's', 'c', now(), now());

INSERT INTO users (email, normalized_email, security_stamp, concurrency_stamp, created_at, updated_at)
VALUES ('bob@example.com', 'BOB@EXAMPLE.COM', 's', 'c', now(), now());

INSERT INTO users (email, normalized_email, security_stamp, concurrency_stamp, created_at, updated_at)
VALUES ('carol@example.com', 'CAROL@EXAMPLE.COM', 's', 'c', now(), now());

INSERT INTO organizations (slug, name, created_by)
SELECT 'org-1', 'Org', id FROM users WHERE email = 'alice@example.com';

INSERT INTO teams (organization_id, slug, name, created_by)
SELECT o.id, 'alpha', 'Alpha', u.id
FROM organizations o, users u
WHERE o.slug = 'org-1' AND u.email = 'alice@example.com';

INSERT INTO projects (team_id, slug, name, created_by)
SELECT t.id, 'backend', 'Backend', u.id
FROM teams t, users u
WHERE t.slug = 'alpha' AND u.email = 'alice@example.com';

INSERT INTO project_members (user_id, project_id, role, added_by)
SELECT bob.id, p.id, 'member', carol.id
FROM users alice, users bob, users carol, projects p
WHERE alice.email = 'alice@example.com'
  AND bob.email = 'bob@example.com'
  AND carol.email = 'carol@example.com'
  AND p.slug = 'backend';

-- ---------------------------------------------------------------------------
-- Cascade scenario A — deleting the parent project removes its memberships.
-- ---------------------------------------------------------------------------
SAVEPOINT cascade_parent;

DELETE FROM projects WHERE slug = 'backend';

SELECT is(
    (SELECT COUNT(*)::int FROM project_members),
    0,
    'deleting a project cascades its membership rows'
);

ROLLBACK TO SAVEPOINT cascade_parent;

-- ---------------------------------------------------------------------------
-- Cascade scenario B — deleting the member user removes their membership rows.
-- ---------------------------------------------------------------------------
SAVEPOINT cascade_member;

DELETE FROM users WHERE email = 'bob@example.com';

SELECT is(
    (SELECT COUNT(*)::int FROM project_members),
    0,
    'deleting a member user cascades their project_members rows'
);

ROLLBACK TO SAVEPOINT cascade_member;

-- ---------------------------------------------------------------------------
-- Restrict scenario C — deleting the inviter (added_by) is blocked.
-- ---------------------------------------------------------------------------
SAVEPOINT restrict_added_by;
SELECT throws_ok(
    $$DELETE FROM users WHERE email = 'carol@example.com'$$,
    '23001',
    NULL,
    'deleting an inviter is blocked by added_by RESTRICT FK (SQLSTATE 23001)'
);
ROLLBACK TO SAVEPOINT restrict_added_by;

SELECT * FROM finish();

ROLLBACK;
