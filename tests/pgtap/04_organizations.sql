-- 04_organizations.sql
-- pgTAP coverage for the organizations table created by
-- src/Heimdall.DAL/Migrations/M202605050010_CreateOrganizations.cs
-- (Phase 2.1 step 1 of docs/proposals/team-collaboration.md §4).
--
-- Asserts:
--   * the table exists with all 5 expected columns and correct types / nullability
--   * id is the primary key
--   * slug is unique (case-insensitively, via citext)
--   * created_at defaults to now() and is NOT NULL
--   * created_by has a RESTRICT FK to users(id)
--   * citext slug rejects case-different duplicates
--
-- Wrapped in BEGIN ... ROLLBACK so it leaves no residue.

BEGIN;

SELECT plan(22);

-- ---------------------------------------------------------------------------
-- Table & columns exist
-- ---------------------------------------------------------------------------
SELECT has_table('organizations');

SELECT has_column('organizations', 'id');
SELECT has_column('organizations', 'slug');
SELECT has_column('organizations', 'name');
SELECT has_column('organizations', 'created_at');
SELECT has_column('organizations', 'created_by');

-- ---------------------------------------------------------------------------
-- Column types
-- ---------------------------------------------------------------------------
SELECT col_type_is('organizations', 'id', 'uuid');
SELECT col_type_is('organizations', 'slug', 'citext');
SELECT col_type_is('organizations', 'name', 'text');
SELECT col_type_is('organizations', 'created_at', 'timestamp with time zone');
SELECT col_type_is('organizations', 'created_by', 'uuid');

-- ---------------------------------------------------------------------------
-- Nullability
-- ---------------------------------------------------------------------------
SELECT col_not_null('organizations', 'id');
SELECT col_not_null('organizations', 'slug');
SELECT col_not_null('organizations', 'name');
SELECT col_not_null('organizations', 'created_at');
SELECT col_not_null('organizations', 'created_by');

-- ---------------------------------------------------------------------------
-- Primary key, unique index, and supporting indexes
-- ---------------------------------------------------------------------------
SELECT col_is_pk('organizations', 'id');
SELECT index_is_unique('ux_organizations_slug');
SELECT has_index('organizations', 'ix_organizations_created_by');

-- ---------------------------------------------------------------------------
-- citext behaviour: case-different slugs collide on the unique index, and the
-- created_by FK rejects unknown user ids. Each is wrapped in a SAVEPOINT so an
-- expected error does not abort the surrounding transaction.
-- ---------------------------------------------------------------------------

-- Seed a user we can attribute the orgs to.
INSERT INTO users (email, normalized_email, security_stamp, concurrency_stamp, created_at, updated_at)
VALUES ('owner@example.com', 'OWNER@EXAMPLE.COM', 's', 'c', now(), now());

SAVEPOINT citext_check;

SELECT lives_ok(
    $$
    INSERT INTO organizations (slug, name, created_by)
    SELECT 'Heimdall', 'Heimdall', id FROM users WHERE email = 'owner@example.com'
    $$,
    'initial insert with mixed-case slug succeeds'
);

SELECT throws_ok(
    $$
    INSERT INTO organizations (slug, name, created_by)
    SELECT 'heimdall', 'Heimdall (dup)', id FROM users WHERE email = 'owner@example.com'
    $$,
    '23505',
    NULL,
    'lower-case duplicate slug violates citext unique index (case-insensitive)'
);

ROLLBACK TO SAVEPOINT citext_check;

SAVEPOINT fk_check;

SELECT throws_ok(
    $$
    INSERT INTO organizations (slug, name, created_by)
    VALUES ('orphan', 'Orphan', '00000000-0000-0000-0000-000000000000')
    $$,
    '23503',
    NULL,
    'created_by FK rejects unknown user id'
);

ROLLBACK TO SAVEPOINT fk_check;

SELECT * FROM finish();

ROLLBACK;
