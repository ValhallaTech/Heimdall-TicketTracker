-- 20_mfa.sql
-- pgTAP coverage for the Phase 4.1 MFA schema, created by:
--   * src/Heimdall.DAL/Migrations/M202605120001_AddUsersTwoFactorEnabled.cs   (step 1)
--   * src/Heimdall.DAL/Migrations/M202605120002_CreateUserAuthenticatorKeys.cs (step 2)
--   * src/Heimdall.DAL/Migrations/M202605120003_CreateUserRecoveryCodes.cs    (step 3)
-- per docs/implementation/phase-4-checklist.md (steps 1, 2, 3) and
-- docs/proposals/security-and-authorization.md §9.3.
--
-- Asserts:
--   * users.two_factor_enabled exists as boolean NOT NULL DEFAULT false
--   * user_authenticator_keys exists with all 4 columns, correct types, every
--     column NOT NULL, user_id as PK, FK user_id -> users(id), created_at
--     defaults to now(); deleting the parent user cascades the row
--   * user_recovery_codes exists with all 5 columns, correct types, correct
--     nullability (used_at nullable, the rest NOT NULL), id as PK with default
--     gen_random_uuid(), created_at default now(), FK user_id -> users(id),
--     composite index ix_user_recovery_codes_user_id_used_at on (user_id, used_at);
--     deleting the parent user cascades the rows
--
-- Wrapped in BEGIN ... ROLLBACK so it leaves no residue.

BEGIN;

SELECT plan(44);

-- ===========================================================================
-- Step 1 — users.two_factor_enabled
-- ===========================================================================
SELECT has_column('users', 'two_factor_enabled');
SELECT col_type_is('users', 'two_factor_enabled', 'boolean');
SELECT col_not_null('users', 'two_factor_enabled');
SELECT col_default_is('users', 'two_factor_enabled', 'false');

-- ===========================================================================
-- Step 2 — user_authenticator_keys
-- ===========================================================================

-- ---------------------------------------------------------------------------
-- Table & columns exist
-- ---------------------------------------------------------------------------
SELECT has_table('user_authenticator_keys');

SELECT has_column('user_authenticator_keys', 'user_id');
SELECT has_column('user_authenticator_keys', 'provider_name');
SELECT has_column('user_authenticator_keys', 'authenticator_key');
SELECT has_column('user_authenticator_keys', 'created_at');

-- ---------------------------------------------------------------------------
-- Column types
-- ---------------------------------------------------------------------------
SELECT col_type_is('user_authenticator_keys', 'user_id',           'uuid');
SELECT col_type_is('user_authenticator_keys', 'provider_name',     'text');
SELECT col_type_is('user_authenticator_keys', 'authenticator_key', 'text');
SELECT col_type_is('user_authenticator_keys', 'created_at',        'timestamp with time zone');

-- ---------------------------------------------------------------------------
-- Nullability — every column is NOT NULL.
-- ---------------------------------------------------------------------------
SELECT col_not_null('user_authenticator_keys', 'user_id');
SELECT col_not_null('user_authenticator_keys', 'provider_name');
SELECT col_not_null('user_authenticator_keys', 'authenticator_key');
SELECT col_not_null('user_authenticator_keys', 'created_at');

-- ---------------------------------------------------------------------------
-- Primary key & foreign key
-- ---------------------------------------------------------------------------
SELECT col_is_pk('user_authenticator_keys', 'user_id');
SELECT fk_ok('user_authenticator_keys', 'user_id', 'users', 'id');

-- ---------------------------------------------------------------------------
-- Default — created_at is server-clock.
-- ---------------------------------------------------------------------------
SELECT col_default_is('user_authenticator_keys', 'created_at', 'now()');

-- ===========================================================================
-- Step 3 — user_recovery_codes
-- ===========================================================================

-- ---------------------------------------------------------------------------
-- Table & columns exist
-- ---------------------------------------------------------------------------
SELECT has_table('user_recovery_codes');

SELECT has_column('user_recovery_codes', 'id');
SELECT has_column('user_recovery_codes', 'user_id');
SELECT has_column('user_recovery_codes', 'code_hash');
SELECT has_column('user_recovery_codes', 'used_at');
SELECT has_column('user_recovery_codes', 'created_at');

-- ---------------------------------------------------------------------------
-- Column types
-- ---------------------------------------------------------------------------
SELECT col_type_is('user_recovery_codes', 'id',         'uuid');
SELECT col_type_is('user_recovery_codes', 'user_id',    'uuid');
SELECT col_type_is('user_recovery_codes', 'code_hash',  'text');
SELECT col_type_is('user_recovery_codes', 'used_at',    'timestamp with time zone');
SELECT col_type_is('user_recovery_codes', 'created_at', 'timestamp with time zone');

-- ---------------------------------------------------------------------------
-- Nullability — used_at is the only nullable column (NULL = unredeemed).
-- ---------------------------------------------------------------------------
SELECT col_not_null('user_recovery_codes', 'id');
SELECT col_not_null('user_recovery_codes', 'user_id');
SELECT col_not_null('user_recovery_codes', 'code_hash');
SELECT col_is_null( 'user_recovery_codes', 'used_at');
SELECT col_not_null('user_recovery_codes', 'created_at');

-- ---------------------------------------------------------------------------
-- Primary key, foreign key, supporting composite index
-- ---------------------------------------------------------------------------
SELECT col_is_pk('user_recovery_codes', 'id');
SELECT fk_ok('user_recovery_codes', 'user_id', 'users', 'id');
SELECT has_index(
    'user_recovery_codes',
    'ix_user_recovery_codes_user_id_used_at',
    ARRAY['user_id', 'used_at']
);

-- ---------------------------------------------------------------------------
-- Defaults — id is gen_random_uuid(), created_at is now().
-- ---------------------------------------------------------------------------
SELECT col_default_is('user_recovery_codes', 'id',         'gen_random_uuid()');
SELECT col_default_is('user_recovery_codes', 'created_at', 'now()');

-- ===========================================================================
-- Behavioural cascade probes — seed a user with one authenticator key and two
-- recovery codes, then exercise each ON DELETE CASCADE in a SAVEPOINT so the
-- outer transaction stays clean.
-- ===========================================================================
INSERT INTO users (email, normalized_email, security_stamp, concurrency_stamp, created_at, updated_at)
VALUES ('mfa@example.com', 'MFA@EXAMPLE.COM', 's', 'c', now(), now());

-- Stash the seed user's id in a transaction-scoped GUC so the cascade probes
-- below can scope their COUNT(*) assertions to just this user — keeps the
-- pgTAP suite robust if other scripts ever leave residual rows in these
-- tables (the outer BEGIN/ROLLBACK already protects us, but defence in
-- depth is cheap and matches the per-user filtering used elsewhere).
SELECT set_config('test.mfa_user_id', id::text, false)
FROM users WHERE email = 'mfa@example.com';

INSERT INTO user_authenticator_keys (user_id, provider_name, authenticator_key)
SELECT id, 'Authenticator', 'JBSWY3DPEHPK3PXP'
FROM users WHERE email = 'mfa@example.com';

INSERT INTO user_recovery_codes (user_id, code_hash)
SELECT id, 'hash-1' FROM users WHERE email = 'mfa@example.com';

INSERT INTO user_recovery_codes (user_id, code_hash)
SELECT id, 'hash-2' FROM users WHERE email = 'mfa@example.com';

-- ---------------------------------------------------------------------------
-- Cascade scenario A — deleting the user removes their authenticator key.
-- ---------------------------------------------------------------------------
SAVEPOINT cascade_authenticator;

SELECT lives_ok(
    $$DELETE FROM users WHERE email = 'mfa@example.com'$$,
    'deleting the MFA user succeeds (no RESTRICT FKs block the delete)'
);

SELECT is(
    (SELECT COUNT(*)::int FROM user_authenticator_keys
        WHERE user_id = current_setting('test.mfa_user_id')::uuid),
    0,
    'deleting a user cascades their user_authenticator_keys row'
);

ROLLBACK TO SAVEPOINT cascade_authenticator;

-- ---------------------------------------------------------------------------
-- Cascade scenario B — deleting the user removes their recovery codes.
-- ---------------------------------------------------------------------------
SAVEPOINT cascade_recovery_codes;

DELETE FROM users WHERE email = 'mfa@example.com';

SELECT is(
    (SELECT COUNT(*)::int FROM user_recovery_codes
        WHERE user_id = current_setting('test.mfa_user_id')::uuid),
    0,
    'deleting a user cascades their user_recovery_codes rows'
);

ROLLBACK TO SAVEPOINT cascade_recovery_codes;

SELECT * FROM finish();

ROLLBACK;
