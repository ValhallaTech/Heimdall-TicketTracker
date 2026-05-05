-- 01_users.sql
-- pgTAP coverage for the users table created by
-- src/Heimdall.DAL/Migrations/M202605050001_CreateUsers.cs (Phase 1 step 1 of the
-- Authenticated Foundation, per docs/proposals/security-and-authorization.md §9.3).
--
-- Asserts:
--   * the table exists with all 13 expected columns and correct types / nullability
--   * id is the primary key
--   * email and normalized_email are unique
--   * boolean / integer columns carry the documented defaults
--   * created_at / updated_at are NOT NULL with no default (caller sets them)
--   * the email columns are genuinely citext — case-different inserts collide on the
--     unique constraint
--
-- Wrapped in BEGIN ... ROLLBACK so it leaves no residue.

BEGIN;

SELECT plan(51);

-- ---------------------------------------------------------------------------
-- Table & columns exist
-- ---------------------------------------------------------------------------
SELECT has_table('users');

SELECT has_column('users', 'id');
SELECT has_column('users', 'email');
SELECT has_column('users', 'normalized_email');
SELECT has_column('users', 'password_hash');
SELECT has_column('users', 'security_stamp');
SELECT has_column('users', 'concurrency_stamp');
SELECT has_column('users', 'email_confirmed');
SELECT has_column('users', 'lockout_end');
SELECT has_column('users', 'lockout_enabled');
SELECT has_column('users', 'access_failed_count');
SELECT has_column('users', 'system_admin');
SELECT has_column('users', 'created_at');
SELECT has_column('users', 'updated_at');

-- ---------------------------------------------------------------------------
-- Column types
-- ---------------------------------------------------------------------------
SELECT col_type_is('users', 'id', 'uuid');
SELECT col_type_is('users', 'email', 'citext');
SELECT col_type_is('users', 'normalized_email', 'citext');
SELECT col_type_is('users', 'password_hash', 'text');
SELECT col_type_is('users', 'security_stamp', 'text');
SELECT col_type_is('users', 'concurrency_stamp', 'text');
SELECT col_type_is('users', 'email_confirmed', 'boolean');
SELECT col_type_is('users', 'lockout_end', 'timestamp with time zone');
SELECT col_type_is('users', 'lockout_enabled', 'boolean');
SELECT col_type_is('users', 'access_failed_count', 'integer');
SELECT col_type_is('users', 'system_admin', 'boolean');
SELECT col_type_is('users', 'created_at', 'timestamp with time zone');
SELECT col_type_is('users', 'updated_at', 'timestamp with time zone');

-- ---------------------------------------------------------------------------
-- Nullability
-- ---------------------------------------------------------------------------
SELECT col_not_null('users', 'id');
SELECT col_not_null('users', 'email');
SELECT col_not_null('users', 'normalized_email');
SELECT col_is_null('users', 'password_hash');
SELECT col_not_null('users', 'security_stamp');
SELECT col_not_null('users', 'concurrency_stamp');
SELECT col_not_null('users', 'email_confirmed');
SELECT col_is_null('users', 'lockout_end');
SELECT col_not_null('users', 'lockout_enabled');
SELECT col_not_null('users', 'access_failed_count');
SELECT col_not_null('users', 'system_admin');
SELECT col_not_null('users', 'created_at');
SELECT col_not_null('users', 'updated_at');

-- ---------------------------------------------------------------------------
-- Primary key & unique constraints
-- ---------------------------------------------------------------------------
SELECT col_is_pk('users', 'id');
SELECT col_is_unique('users', 'email');
SELECT col_is_unique('users', 'normalized_email');

-- ---------------------------------------------------------------------------
-- Defaults
-- ---------------------------------------------------------------------------
SELECT col_default_is('users', 'email_confirmed',     'false');
SELECT col_default_is('users', 'lockout_enabled',     'true');
SELECT col_default_is('users', 'access_failed_count', 0);
SELECT col_default_is('users', 'system_admin',        'false');

-- created_at / updated_at must have no default — the application owns these timestamps.
SELECT col_hasnt_default('users', 'created_at');
SELECT col_hasnt_default('users', 'updated_at');

-- ---------------------------------------------------------------------------
-- citext behaviour: case-different emails must collide on the unique index.
-- Wrapped in a SAVEPOINT so that the expected unique-violation does not abort
-- the surrounding test transaction.
-- ---------------------------------------------------------------------------
SAVEPOINT citext_check;

SELECT lives_ok(
    $$
    INSERT INTO users
        (email, normalized_email, security_stamp, concurrency_stamp, created_at, updated_at)
    VALUES
        ('Foo@Example.com', 'FOO@EXAMPLE.COM', 'stamp-1', 'concurrency-1', now(), now())
    $$,
    'initial insert with mixed-case email succeeds'
);

SELECT throws_ok(
    $$
    INSERT INTO users
        (email, normalized_email, security_stamp, concurrency_stamp, created_at, updated_at)
    VALUES
        ('foo@example.com', 'foo@example.com', 'stamp-2', 'concurrency-2', now(), now())
    $$,
    '23505',
    NULL,
    'lower-case duplicate violates citext unique index (case-insensitive)'
);

ROLLBACK TO SAVEPOINT citext_check;

SELECT * FROM finish();

ROLLBACK;
