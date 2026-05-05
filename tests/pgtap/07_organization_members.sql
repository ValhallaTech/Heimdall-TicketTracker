-- 07_organization_members.sql
-- pgTAP coverage for the organization_members table and org_member_role enum
-- created by src/Heimdall.DAL/Migrations/M202605050013_CreateOrganizationMembers.cs
-- (Phase 2.2 step 4 of docs/proposals/team-collaboration.md §3.1 + §4).
--
-- Asserts:
--   * the table exists with all 5 expected columns and correct types / nullability
--   * added_at defaults to now()
--   * (user_id, organization_id) is the composite primary key
--   * supporting indexes on organization_id and added_by exist
--   * org_member_role enum carries owner/admin/member/viewer in that declared order
--   * deleting the parent organization cascades the membership rows
--   * deleting the member user cascades their membership rows
--   * deleting the inviter (added_by) is blocked by the RESTRICT FK
--
-- Wrapped in BEGIN ... ROLLBACK so it leaves no residue.

BEGIN;

SELECT plan(25);

-- ---------------------------------------------------------------------------
-- Table & columns exist
-- ---------------------------------------------------------------------------
SELECT has_table('organization_members');

SELECT has_column('organization_members', 'user_id');
SELECT has_column('organization_members', 'organization_id');
SELECT has_column('organization_members', 'role');
SELECT has_column('organization_members', 'added_at');
SELECT has_column('organization_members', 'added_by');

-- ---------------------------------------------------------------------------
-- Column types
-- ---------------------------------------------------------------------------
SELECT col_type_is('organization_members', 'user_id', 'uuid');
SELECT col_type_is('organization_members', 'organization_id', 'uuid');
SELECT col_type_is('organization_members', 'role', 'org_member_role');
SELECT col_type_is('organization_members', 'added_at', 'timestamp with time zone');
SELECT col_type_is('organization_members', 'added_by', 'uuid');

-- ---------------------------------------------------------------------------
-- Nullability — every column is NOT NULL.
-- ---------------------------------------------------------------------------
SELECT col_not_null('organization_members', 'user_id');
SELECT col_not_null('organization_members', 'organization_id');
SELECT col_not_null('organization_members', 'role');
SELECT col_not_null('organization_members', 'added_at');
SELECT col_not_null('organization_members', 'added_by');

-- ---------------------------------------------------------------------------
-- Default — added_at is server-clock (matches OrganizationMemberRepository's
-- INSERT, which omits the column from the column list).
-- ---------------------------------------------------------------------------
SELECT col_has_default('organization_members', 'added_at');
SELECT col_default_is('organization_members', 'added_at', 'now()');

-- ---------------------------------------------------------------------------
-- Composite primary key. The existing pgTAP files only use the single-column
-- col_is_pk overload; querying pg_constraint directly is the version-agnostic
-- way to assert the column set of a composite PK.
-- ---------------------------------------------------------------------------
SELECT is(
    (SELECT array_agg(a.attname ORDER BY a.attnum)
     FROM pg_constraint c
     JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = ANY(c.conkey)
     WHERE c.conrelid = 'organization_members'::regclass AND c.contype = 'p'),
    ARRAY['user_id','organization_id']::name[],
    'composite primary key is (user_id, organization_id)'
);

-- ---------------------------------------------------------------------------
-- Supporting indexes
-- ---------------------------------------------------------------------------
SELECT has_index('organization_members', 'ix_organization_members_organization_id');
SELECT has_index('organization_members', 'ix_organization_members_added_by');

-- ---------------------------------------------------------------------------
-- Enum value list — values AND declared order are pinned (OpenFGA in Phase 3
-- consumes these wire strings directly).
-- ---------------------------------------------------------------------------
SELECT is(
    (SELECT array_agg(enumlabel::text ORDER BY enumsortorder)
     FROM pg_enum e
     JOIN pg_type t ON e.enumtypid = t.oid
     WHERE t.typname = 'org_member_role'),
    ARRAY['owner','admin','member','viewer']::text[],
    'org_member_role enum values in declared order'
);

-- ---------------------------------------------------------------------------
-- Seed three users (alice as org owner, bob as member, carol as the inviter
-- that is *not* the org owner) and an org owned by alice. Using a dedicated
-- carol means the restrict-FK assertion below isolates the
-- organization_members.added_by RESTRICT path; otherwise deleting alice would
-- also trip organizations.created_by and the test would pass for the wrong
-- reason.
-- ---------------------------------------------------------------------------
INSERT INTO users (email, normalized_email, security_stamp, concurrency_stamp, created_at, updated_at)
VALUES ('alice@example.com', 'ALICE@EXAMPLE.COM', 's', 'c', now(), now());

INSERT INTO users (email, normalized_email, security_stamp, concurrency_stamp, created_at, updated_at)
VALUES ('bob@example.com', 'BOB@EXAMPLE.COM', 's', 'c', now(), now());

INSERT INTO users (email, normalized_email, security_stamp, concurrency_stamp, created_at, updated_at)
VALUES ('carol@example.com', 'CAROL@EXAMPLE.COM', 's', 'c', now(), now());

INSERT INTO organizations (slug, name, created_by)
SELECT 'org-1', 'Org', id FROM users WHERE email = 'alice@example.com';

INSERT INTO organization_members (user_id, organization_id, role, added_by)
SELECT bob.id, o.id, 'member', carol.id
FROM users alice, users bob, users carol, organizations o
WHERE alice.email = 'alice@example.com'
  AND bob.email = 'bob@example.com'
  AND carol.email = 'carol@example.com'
  AND o.slug = 'org-1';

-- ---------------------------------------------------------------------------
-- Cascade scenario A — deleting the parent organization removes its memberships.
-- ---------------------------------------------------------------------------
SAVEPOINT cascade_parent;

DELETE FROM organizations WHERE slug = 'org-1';

SELECT is(
    (SELECT COUNT(*)::int FROM organization_members),
    0,
    'deleting an organization cascades its membership rows'
);

ROLLBACK TO SAVEPOINT cascade_parent;

-- ---------------------------------------------------------------------------
-- Cascade scenario B — deleting the member user removes their membership rows.
-- ---------------------------------------------------------------------------
SAVEPOINT cascade_member;

DELETE FROM users WHERE email = 'bob@example.com';

SELECT is(
    (SELECT COUNT(*)::int FROM organization_members),
    0,
    'deleting a member user cascades their membership rows'
);

ROLLBACK TO SAVEPOINT cascade_member;

-- ---------------------------------------------------------------------------
-- Restrict scenario C — deleting the inviter (added_by) is blocked.
-- ---------------------------------------------------------------------------
SAVEPOINT restrict_added_by;
SELECT throws_ok(
    $$DELETE FROM users WHERE email = 'carol@example.com'$$,
    '23503',
    NULL,
    'deleting an inviter is blocked by added_by RESTRICT FK (SQLSTATE 23503)'
);
ROLLBACK TO SAVEPOINT restrict_added_by;

SELECT * FROM finish();

ROLLBACK;
