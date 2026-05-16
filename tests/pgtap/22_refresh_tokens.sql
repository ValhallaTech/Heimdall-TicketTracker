-- 22_refresh_tokens.sql
-- pgTAP coverage for the Phase 5.2 refresh-token schema, created by:
--   * src/Heimdall.DAL/Migrations/M202605200001_CreateRefreshTokens.cs (step 4)
-- per docs/implementation/phase-5-checklist.md (step 4) and
-- docs/proposals/security-and-authorization.md §5.1, §5.2.
--
-- Note on token_hash: the migration's UNIQUE constraint on token_hash is the
-- correct invariant because refresh tokens are stored as the *deterministic*
-- SHA-256 hex digest of the high-entropy random plaintext (refresh tokens are
-- 256-bit server-generated secrets; the salted IPasswordHasher precedent used
-- for Phase 4 recovery codes does not transfer because those are short
-- low-entropy human inputs). Deterministic hashing is also what makes the
-- step-5 GetByHashAsync equality lookup correct, so this file's
-- col_is_unique('refresh_tokens', 'token_hash') assertion is locked in.
--
-- Asserts:
--   * refresh_tokens exists with all 10 columns at the right types and
--     nullability (parent_id, replaced_by, revoked_at, revoked_reason are
--     the only nullable columns).
--   * id is the primary key; token_hash is UNIQUE.
--   * FK user_id    -> users(id)          ON DELETE CASCADE.
--   * FK parent_id  -> refresh_tokens(id) ON DELETE SET NULL.
--   * FK replaced_by-> refresh_tokens(id) ON DELETE SET NULL.
--   * revoked_reason CHECK accepts each of 'rotated' / 'logout' /
--     'family_replay' / 'admin_revoke' and rejects 'foo'.
--   * ix_refresh_tokens_user_family on (user_id, family_id) exists.
--   * ix_refresh_tokens_expires_active is a partial index on (expires_at)
--     with the 'WHERE revoked_at IS NULL' predicate (LIKE check on
--     pg_indexes.indexdef — same approach as 21_signing_keys.sql).
--   * Behavioural probe: insert two rows, set replaced_by on the first to
--     the second's id, DELETE the second row, and assert the first row's
--     replaced_by is NULL (the ON DELETE SET NULL chain-break).
--
-- Wrapped in BEGIN ... ROLLBACK so it leaves no residue.

BEGIN;

SELECT plan(48);

-- ===========================================================================
-- Table & columns exist
-- ===========================================================================
SELECT has_table('refresh_tokens');

SELECT has_column('refresh_tokens', 'id');
SELECT has_column('refresh_tokens', 'user_id');
SELECT has_column('refresh_tokens', 'token_hash');
SELECT has_column('refresh_tokens', 'family_id');
SELECT has_column('refresh_tokens', 'parent_id');
SELECT has_column('refresh_tokens', 'replaced_by');
SELECT has_column('refresh_tokens', 'issued_at');
SELECT has_column('refresh_tokens', 'expires_at');
SELECT has_column('refresh_tokens', 'revoked_at');
SELECT has_column('refresh_tokens', 'revoked_reason');

-- ===========================================================================
-- Column types
-- ===========================================================================
SELECT col_type_is('refresh_tokens', 'id',             'uuid');
SELECT col_type_is('refresh_tokens', 'user_id',        'uuid');
SELECT col_type_is('refresh_tokens', 'token_hash',     'text');
SELECT col_type_is('refresh_tokens', 'family_id',      'uuid');
SELECT col_type_is('refresh_tokens', 'parent_id',      'uuid');
SELECT col_type_is('refresh_tokens', 'replaced_by',    'uuid');
SELECT col_type_is('refresh_tokens', 'issued_at',      'timestamp with time zone');
SELECT col_type_is('refresh_tokens', 'expires_at',     'timestamp with time zone');
SELECT col_type_is('refresh_tokens', 'revoked_at',     'timestamp with time zone');
SELECT col_type_is('refresh_tokens', 'revoked_reason', 'text');

-- ===========================================================================
-- Nullability — parent_id / replaced_by / revoked_at / revoked_reason are
-- the only nullable columns.
-- ===========================================================================
SELECT col_not_null('refresh_tokens', 'id');
SELECT col_not_null('refresh_tokens', 'user_id');
SELECT col_not_null('refresh_tokens', 'token_hash');
SELECT col_not_null('refresh_tokens', 'family_id');
SELECT col_not_null('refresh_tokens', 'issued_at');
SELECT col_not_null('refresh_tokens', 'expires_at');
SELECT col_is_null( 'refresh_tokens', 'parent_id');
SELECT col_is_null( 'refresh_tokens', 'replaced_by');
SELECT col_is_null( 'refresh_tokens', 'revoked_at');
SELECT col_is_null( 'refresh_tokens', 'revoked_reason');

-- ===========================================================================
-- Primary key and UNIQUE constraint.
-- ===========================================================================
SELECT col_is_pk('refresh_tokens', 'id');
SELECT col_is_unique('refresh_tokens', 'token_hash');

-- ===========================================================================
-- Foreign keys — column targets.
-- ===========================================================================
SELECT fk_ok('refresh_tokens', 'user_id',     'users',           'id');
SELECT fk_ok('refresh_tokens', 'parent_id',   'refresh_tokens',  'id');
SELECT fk_ok('refresh_tokens', 'replaced_by', 'refresh_tokens',  'id');

-- ===========================================================================
-- Foreign-key ON DELETE actions — confdeltype is 'c' for CASCADE, 'n' for
-- SET NULL. We identify each single-column FK by its conkey[1] attname so
-- the assertion does not depend on the FK constraint's auto-generated name.
-- ===========================================================================
SELECT is(
    (SELECT c.confdeltype::text
        FROM pg_constraint c
        WHERE c.conrelid = 'public.refresh_tokens'::regclass
          AND c.contype  = 'f'
          AND (SELECT attname FROM pg_attribute
                  WHERE attrelid = c.conrelid AND attnum = c.conkey[1]) = 'user_id'),
    'c',
    'FK user_id -> users(id) is ON DELETE CASCADE'
);

SELECT is(
    (SELECT c.confdeltype::text
        FROM pg_constraint c
        WHERE c.conrelid = 'public.refresh_tokens'::regclass
          AND c.contype  = 'f'
          AND (SELECT attname FROM pg_attribute
                  WHERE attrelid = c.conrelid AND attnum = c.conkey[1]) = 'parent_id'),
    'n',
    'FK parent_id -> refresh_tokens(id) is ON DELETE SET NULL'
);

SELECT is(
    (SELECT c.confdeltype::text
        FROM pg_constraint c
        WHERE c.conrelid = 'public.refresh_tokens'::regclass
          AND c.contype  = 'f'
          AND (SELECT attname FROM pg_attribute
                  WHERE attrelid = c.conrelid AND attnum = c.conkey[1]) = 'replaced_by'),
    'n',
    'FK replaced_by -> refresh_tokens(id) is ON DELETE SET NULL'
);

-- ===========================================================================
-- Indexes — composite (user_id, family_id) and partial (expires_at).
-- ===========================================================================
SELECT has_index(
    'refresh_tokens',
    'ix_refresh_tokens_user_family',
    ARRAY['user_id', 'family_id']
);

SELECT has_index(
    'refresh_tokens',
    'ix_refresh_tokens_expires_active',
    ARRAY['expires_at']
);

-- pg_indexes.indexdef carries the predicate text. Assert the canonical
-- "WHERE (revoked_at IS NULL)" form Postgres emits via plain SQL LIKE —
-- same approach 21_signing_keys.sql uses for ix_signing_keys_not_after_active.
SELECT ok(
    (SELECT indexdef FROM pg_indexes
        WHERE schemaname = 'public'
          AND indexname  = 'ix_refresh_tokens_expires_active')
    LIKE '%WHERE (revoked_at IS NULL)%',
    'ix_refresh_tokens_expires_active is partial on revoked_at IS NULL'
);

-- ===========================================================================
-- Seed user for the CHECK and behavioural probes.
-- ===========================================================================
INSERT INTO users (email, normalized_email, security_stamp, concurrency_stamp, created_at, updated_at)
VALUES ('refresh@example.com', 'REFRESH@EXAMPLE.COM', 's', 'c', now(), now());

SELECT set_config('test.refresh_user_id', id::text, false)
FROM users WHERE email = 'refresh@example.com';

-- ===========================================================================
-- revoked_reason CHECK — accepts each of the four allowed values and
-- rejects 'foo'. Each probe runs in a SAVEPOINT so it leaves no residue.
-- token_hash values are unique-per-probe to avoid colliding with the
-- UNIQUE constraint when the savepoints stack up against the same
-- transaction snapshot.
-- ===========================================================================
SAVEPOINT reason_rotated;
SELECT lives_ok(
    $$INSERT INTO refresh_tokens (id, user_id, token_hash, family_id, expires_at, revoked_at, revoked_reason)
      VALUES (gen_random_uuid(),
              current_setting('test.refresh_user_id')::uuid,
              'h-rotated',
              gen_random_uuid(),
              now() + interval '14 days',
              now(),
              'rotated')$$,
    'revoked_reason CHECK accepts ''rotated'''
);
ROLLBACK TO SAVEPOINT reason_rotated;

SAVEPOINT reason_logout;
SELECT lives_ok(
    $$INSERT INTO refresh_tokens (id, user_id, token_hash, family_id, expires_at, revoked_at, revoked_reason)
      VALUES (gen_random_uuid(),
              current_setting('test.refresh_user_id')::uuid,
              'h-logout',
              gen_random_uuid(),
              now() + interval '14 days',
              now(),
              'logout')$$,
    'revoked_reason CHECK accepts ''logout'''
);
ROLLBACK TO SAVEPOINT reason_logout;

SAVEPOINT reason_replay;
SELECT lives_ok(
    $$INSERT INTO refresh_tokens (id, user_id, token_hash, family_id, expires_at, revoked_at, revoked_reason)
      VALUES (gen_random_uuid(),
              current_setting('test.refresh_user_id')::uuid,
              'h-replay',
              gen_random_uuid(),
              now() + interval '14 days',
              now(),
              'family_replay')$$,
    'revoked_reason CHECK accepts ''family_replay'''
);
ROLLBACK TO SAVEPOINT reason_replay;

SAVEPOINT reason_admin;
SELECT lives_ok(
    $$INSERT INTO refresh_tokens (id, user_id, token_hash, family_id, expires_at, revoked_at, revoked_reason)
      VALUES (gen_random_uuid(),
              current_setting('test.refresh_user_id')::uuid,
              'h-admin',
              gen_random_uuid(),
              now() + interval '14 days',
              now(),
              'admin_revoke')$$,
    'revoked_reason CHECK accepts ''admin_revoke'''
);
ROLLBACK TO SAVEPOINT reason_admin;

SAVEPOINT reason_foo;
SELECT throws_ok(
    $$INSERT INTO refresh_tokens (id, user_id, token_hash, family_id, expires_at, revoked_at, revoked_reason)
      VALUES (gen_random_uuid(),
              current_setting('test.refresh_user_id')::uuid,
              'h-foo',
              gen_random_uuid(),
              now() + interval '14 days',
              now(),
              'foo')$$,
    '23514',
    NULL,
    'revoked_reason CHECK rejects arbitrary values'
);
ROLLBACK TO SAVEPOINT reason_foo;

-- ===========================================================================
-- Behavioural probe — ON DELETE SET NULL on replaced_by.
-- Insert two rows in the same family; set replaced_by on the first to the
-- second's id; DELETE the second row; assert the first row's replaced_by
-- is NULL. Wrapped in a SAVEPOINT for symmetry with the CHECK probes.
-- ===========================================================================
SAVEPOINT replaced_by_setnull;

INSERT INTO refresh_tokens (id, user_id, token_hash, family_id, expires_at)
VALUES ('11111111-1111-1111-1111-111111111111',
        current_setting('test.refresh_user_id')::uuid,
        'h-parent',
        '22222222-2222-2222-2222-222222222222',
        now() + interval '14 days');

INSERT INTO refresh_tokens (id, user_id, token_hash, family_id, parent_id, expires_at)
VALUES ('33333333-3333-3333-3333-333333333333',
        current_setting('test.refresh_user_id')::uuid,
        'h-child',
        '22222222-2222-2222-2222-222222222222',
        '11111111-1111-1111-1111-111111111111',
        now() + interval '14 days');

UPDATE refresh_tokens
    SET replaced_by = '33333333-3333-3333-3333-333333333333'
    WHERE id = '11111111-1111-1111-1111-111111111111';

DELETE FROM refresh_tokens WHERE id = '33333333-3333-3333-3333-333333333333';

SELECT is(
    (SELECT replaced_by FROM refresh_tokens
        WHERE id = '11111111-1111-1111-1111-111111111111'),
    NULL::uuid,
    'deleting the replacement row sets the parent''s replaced_by to NULL (ON DELETE SET NULL)'
);

ROLLBACK TO SAVEPOINT replaced_by_setnull;

SELECT * FROM finish();

ROLLBACK;
