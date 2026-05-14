-- 21_signing_keys.sql
-- pgTAP coverage for the Phase 5.1 signing-key schema, created by:
--   * src/Heimdall.DAL/Migrations/M202605130001_CreateSigningKeys.cs (step 1)
-- per docs/implementation/phase-5-checklist.md (steps 1 and 17) and
-- docs/proposals/phase-5-signing-key-hardening.md §2.1, §2.2, §2.4, §2.7.
--
-- Asserts:
--   * signing_keys exists with all 8 columns at the right types and
--     nullability (retired_at is the only nullable column).
--   * kid is the primary key; created_at defaults to now().
--   * The alg CHECK accepts 'RS256' / 'ES256' and rejects everything else
--     ('HS256', 'none', 'foo').
--   * private_key_protected is bytea NOT NULL (hardening §2.7).
--   * Partial index ix_signing_keys_not_after_active exists with the
--     'WHERE retired_at IS NULL' predicate (signing-key selection hot path).
--   * Composite index ix_signing_keys_validity_window on (not_before, not_after).
--   * Row Level Security is enabled on the table (hardening §2.7).
--   * heimdall_app has NO column-level SELECT privilege on
--     private_key_protected — the column-level GRANT is the actual access
--     control; RLS is defence in depth (hardening §2.7).
--   * The trg_signing_keys_audit trigger is wired on signing_keys.
--   * Behavioural probe: INSERT via signing_keys_insert(...) writes exactly
--     one audit_events row of type 'token.signing_key.generated'; UPDATE
--     retired_at = now() writes exactly one 'token.signing_key.retired'.
--
-- Wrapped in BEGIN ... ROLLBACK so it leaves no residue.

BEGIN;

SELECT plan(47);

-- ===========================================================================
-- Table & columns exist
-- ===========================================================================
SELECT has_table('signing_keys');

SELECT has_column('signing_keys', 'kid');
SELECT has_column('signing_keys', 'alg');
SELECT has_column('signing_keys', 'public_jwk');
SELECT has_column('signing_keys', 'private_key_protected');
SELECT has_column('signing_keys', 'created_at');
SELECT has_column('signing_keys', 'not_before');
SELECT has_column('signing_keys', 'not_after');
SELECT has_column('signing_keys', 'retired_at');

-- ===========================================================================
-- Column types — private_key_protected is bytea per hardening §2.1 / §2.7.
-- ===========================================================================
SELECT col_type_is('signing_keys', 'kid',                   'text');
SELECT col_type_is('signing_keys', 'alg',                   'text');
SELECT col_type_is('signing_keys', 'public_jwk',            'jsonb');
SELECT col_type_is('signing_keys', 'private_key_protected', 'bytea');
SELECT col_type_is('signing_keys', 'created_at',            'timestamp with time zone');
SELECT col_type_is('signing_keys', 'not_before',            'timestamp with time zone');
SELECT col_type_is('signing_keys', 'not_after',             'timestamp with time zone');
SELECT col_type_is('signing_keys', 'retired_at',            'timestamp with time zone');

-- ===========================================================================
-- Nullability — every column NOT NULL except retired_at.
-- ===========================================================================
SELECT col_not_null('signing_keys', 'kid');
SELECT col_not_null('signing_keys', 'alg');
SELECT col_not_null('signing_keys', 'public_jwk');
SELECT col_not_null('signing_keys', 'private_key_protected');
SELECT col_not_null('signing_keys', 'created_at');
SELECT col_not_null('signing_keys', 'not_before');
SELECT col_not_null('signing_keys', 'not_after');
SELECT col_is_null( 'signing_keys', 'retired_at');

-- ===========================================================================
-- Primary key & created_at default.
-- ===========================================================================
SELECT col_is_pk('signing_keys', 'kid');
SELECT col_default_is('signing_keys', 'created_at', 'now()');

-- ===========================================================================
-- alg CHECK — RS256 / ES256 accepted; HS256 / 'none' / 'foo' rejected.
-- All probes run inside SAVEPOINTs so they leave no residue.
-- ===========================================================================
SAVEPOINT alg_rs256;
SELECT lives_ok(
    $$INSERT INTO signing_keys (kid, alg, public_jwk, private_key_protected, not_before, not_after)
      VALUES ('kid-rs256', 'RS256', '{}'::jsonb, '\x00'::bytea, now(), now() + interval '90 days')$$,
    'alg CHECK accepts RS256'
);
ROLLBACK TO SAVEPOINT alg_rs256;

SAVEPOINT alg_es256;
SELECT lives_ok(
    $$INSERT INTO signing_keys (kid, alg, public_jwk, private_key_protected, not_before, not_after)
      VALUES ('kid-es256', 'ES256', '{}'::jsonb, '\x00'::bytea, now(), now() + interval '90 days')$$,
    'alg CHECK accepts ES256'
);
ROLLBACK TO SAVEPOINT alg_es256;

SAVEPOINT alg_hs256;
SELECT throws_ok(
    $$INSERT INTO signing_keys (kid, alg, public_jwk, private_key_protected, not_before, not_after)
      VALUES ('kid-hs256', 'HS256', '{}'::jsonb, '\x00'::bytea, now(), now() + interval '90 days')$$,
    '23514',
    NULL,
    'alg CHECK rejects HS256'
);
ROLLBACK TO SAVEPOINT alg_hs256;

SAVEPOINT alg_none;
SELECT throws_ok(
    $$INSERT INTO signing_keys (kid, alg, public_jwk, private_key_protected, not_before, not_after)
      VALUES ('kid-none', 'none', '{}'::jsonb, '\x00'::bytea, now(), now() + interval '90 days')$$,
    '23514',
    NULL,
    'alg CHECK rejects ''none'''
);
ROLLBACK TO SAVEPOINT alg_none;

SAVEPOINT alg_foo;
SELECT throws_ok(
    $$INSERT INTO signing_keys (kid, alg, public_jwk, private_key_protected, not_before, not_after)
      VALUES ('kid-foo', 'foo', '{}'::jsonb, '\x00'::bytea, now(), now() + interval '90 days')$$,
    '23514',
    NULL,
    'alg CHECK rejects arbitrary values'
);
ROLLBACK TO SAVEPOINT alg_foo;

-- ===========================================================================
-- Indexes — partial WHERE retired_at IS NULL, and the composite validity
-- window index.
-- ===========================================================================
SELECT has_index(
    'signing_keys',
    'ix_signing_keys_not_after_active',
    ARRAY['not_after']
);

-- pg_indexes.indexdef carries the predicate text. Assert the canonical
-- "WHERE (retired_at IS NULL)" form Postgres emits via plain SQL LIKE —
-- pgTAP has no like_match() function (was a wrong guess); ok() with a
-- LIKE comparison is the portable replacement.
SELECT ok(
    (SELECT indexdef FROM pg_indexes
        WHERE schemaname = 'public'
          AND indexname = 'ix_signing_keys_not_after_active')
    LIKE '%WHERE (retired_at IS NULL)%',
    'ix_signing_keys_not_after_active is partial on retired_at IS NULL'
);

SELECT has_index(
    'signing_keys',
    'ix_signing_keys_validity_window',
    ARRAY['not_before', 'not_after']
);

-- ===========================================================================
-- Hardening §2.7 — RLS enabled on the table.
-- ===========================================================================
SELECT is(
    (SELECT relrowsecurity FROM pg_class
        WHERE oid = 'public.signing_keys'::regclass),
    true,
    'Row Level Security is enabled on signing_keys'
);

-- ===========================================================================
-- Ownership & RLS-force posture — the SECURITY DEFINER signing_keys_insert()
-- function is owned by heimdall_signer, so for its INSERT body to succeed
-- under RLS the table must be owned by heimdall_signer AND RLS must NOT be
-- FORCEd (which would re-subject the owner to policies). Both facts are
-- asserted so a future migration cannot silently regress them.
-- The trg_signing_keys_audit trigger fires as the invoking role
-- (heimdall_signer for SECURITY-DEFINER inserts, heimdall_app for direct
-- retired_at UPDATEs), so both roles need INSERT on audit_events or the
-- trigger fails with SQLSTATE 42501.
-- ===========================================================================
SELECT is(
    (SELECT relowner::regrole::text FROM pg_class
        WHERE oid = 'public.signing_keys'::regclass),
    'heimdall_signer',
    'signing_keys is owned by heimdall_signer'
);

SELECT ok(
    (SELECT relrowsecurity AND NOT relforcerowsecurity FROM pg_class
        WHERE oid = 'public.signing_keys'::regclass),
    'signing_keys has RLS ENABLEd but NOT FORCEd'
);

SELECT ok(
    has_table_privilege('heimdall_signer', 'audit_events', 'INSERT')
        AND has_table_privilege('heimdall_app', 'audit_events', 'INSERT'),
    'heimdall_signer and heimdall_app both hold INSERT on audit_events'
);

-- ===========================================================================
-- Hardening §2.7 — heimdall_app must NOT hold column-level SELECT on
-- private_key_protected. The column-level GRANT is the primary access
-- control (RLS is row-level only).
-- ===========================================================================
SELECT is(
    (SELECT COUNT(*)::int
        FROM information_schema.column_privileges
        WHERE grantee       = 'heimdall_app'
          AND table_schema  = 'public'
          AND table_name    = 'signing_keys'
          AND column_name   = 'private_key_protected'
          AND privilege_type = 'SELECT'),
    0,
    'heimdall_app has no column-level SELECT on signing_keys.private_key_protected'
);

-- ===========================================================================
-- Least-privilege grants for heimdall_app (PR review hardening, 2026-05-14):
--   * NO direct INSERT — new rows route through signing_keys_insert().
--   * NO direct DELETE — operator-only via admin tooling.
--   * UPDATE limited to retired_at only — kid/alg/public_jwk/not_before/
--     not_after/created_at are effectively immutable after INSERT.
-- These four assertions pin the posture so a future migration cannot
-- silently re-broaden it.
-- ===========================================================================
SELECT ok(
    NOT has_table_privilege('heimdall_app', 'signing_keys', 'INSERT'),
    'heimdall_app does NOT hold INSERT on signing_keys (must use signing_keys_insert())'
);

SELECT ok(
    NOT has_table_privilege('heimdall_app', 'signing_keys', 'DELETE'),
    'heimdall_app does NOT hold DELETE on signing_keys (operator-only)'
);

SELECT ok(
    has_column_privilege('heimdall_app', 'public.signing_keys', 'retired_at', 'UPDATE'),
    'heimdall_app holds UPDATE on signing_keys.retired_at (retirement path)'
);

SELECT ok(
    NOT has_column_privilege('heimdall_app', 'public.signing_keys', 'not_after', 'UPDATE'),
    'heimdall_app does NOT hold UPDATE on signing_keys.not_after (immutable after insert)'
);

-- ===========================================================================
-- Hardening §2.7 — audit trigger exists on signing_keys.
-- ===========================================================================
SELECT is(
    (SELECT COUNT(*)::int
        FROM pg_trigger
        WHERE tgrelid    = 'public.signing_keys'::regclass
          AND tgname     = 'trg_signing_keys_audit'
          AND NOT tgisinternal),
    1,
    'trg_signing_keys_audit trigger exists on signing_keys'
);

-- ===========================================================================
-- Behavioural probes — INSERT via signing_keys_insert(...) writes a
-- 'token.signing_key.generated' audit row; flipping retired_at writes a
-- 'token.signing_key.retired' audit row. Each probe runs in a SAVEPOINT.
-- ===========================================================================

SAVEPOINT audit_insert;

SELECT signing_keys_insert(
    'kid-audit-1',
    'RS256',
    '{"kty":"RSA","kid":"kid-audit-1"}'::jsonb,
    '\xdeadbeef'::bytea,
    now(),
    now() + interval '90 days'
);

SELECT is(
    (SELECT COUNT(*)::int FROM audit_events
        WHERE event_type = 'token.signing_key.generated'
          AND target     = 'kid-audit-1'),
    1,
    'INSERT via signing_keys_insert emits exactly one token.signing_key.generated audit row'
);

-- Now retire that row in the same savepoint.
UPDATE signing_keys SET retired_at = now() WHERE kid = 'kid-audit-1';

SELECT is(
    (SELECT COUNT(*)::int FROM audit_events
        WHERE event_type = 'token.signing_key.retired'
          AND target     = 'kid-audit-1'),
    1,
    'UPDATE setting retired_at emits exactly one token.signing_key.retired audit row'
);

ROLLBACK TO SAVEPOINT audit_insert;

SELECT * FROM finish();

ROLLBACK;
