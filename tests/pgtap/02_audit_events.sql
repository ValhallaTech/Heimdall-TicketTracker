-- 02_audit_events.sql
-- pgTAP coverage for the audit_events table created by
-- src/Heimdall.DAL/Migrations/M202605050002_CreateAuditEvents.cs (Phase 1 step 2 of
-- the Authenticated Foundation, per docs/proposals/security-and-authorization.md §9.3).
--
-- Asserts:
--   * the table exists with all 8 expected columns and correct types / nullability
--   * id is the primary key
--   * payload defaults to '{}'::jsonb and occurred_at defaults to now()
--   * the FK actor_user_id -> users(id) exists with ON DELETE SET NULL
--   * the three supporting indexes exist and the actor/occurred index is partial
--   * functional check: deleting the parent user nulls actor_user_id (ON DELETE SET NULL)
--
-- Wrapped in BEGIN ... ROLLBACK so it leaves no residue.

BEGIN;

SELECT plan(36);

-- ---------------------------------------------------------------------------
-- Table & columns exist
-- ---------------------------------------------------------------------------
SELECT has_table('audit_events');

SELECT has_column('audit_events', 'id');
SELECT has_column('audit_events', 'actor_user_id');
SELECT has_column('audit_events', 'event_type');
SELECT has_column('audit_events', 'target');
SELECT has_column('audit_events', 'ip');
SELECT has_column('audit_events', 'user_agent');
SELECT has_column('audit_events', 'payload');
SELECT has_column('audit_events', 'occurred_at');

-- ---------------------------------------------------------------------------
-- Column types
-- ---------------------------------------------------------------------------
SELECT col_type_is('audit_events', 'id',            'uuid');
SELECT col_type_is('audit_events', 'actor_user_id', 'uuid');
SELECT col_type_is('audit_events', 'event_type',    'text');
SELECT col_type_is('audit_events', 'target',        'text');
SELECT col_type_is('audit_events', 'ip',            'inet');
SELECT col_type_is('audit_events', 'user_agent',    'text');
SELECT col_type_is('audit_events', 'payload',       'jsonb');
SELECT col_type_is('audit_events', 'occurred_at',   'timestamp with time zone');

-- ---------------------------------------------------------------------------
-- Nullability
-- ---------------------------------------------------------------------------
SELECT col_not_null('audit_events', 'id');
SELECT col_is_null ('audit_events', 'actor_user_id');
SELECT col_not_null('audit_events', 'event_type');
SELECT col_is_null ('audit_events', 'target');
SELECT col_is_null ('audit_events', 'ip');
SELECT col_is_null ('audit_events', 'user_agent');
SELECT col_not_null('audit_events', 'payload');
SELECT col_not_null('audit_events', 'occurred_at');

-- ---------------------------------------------------------------------------
-- Primary key & defaults
-- ---------------------------------------------------------------------------
SELECT col_is_pk('audit_events', 'id');

SELECT col_default_is('audit_events', 'payload',     $$'{}'::jsonb$$);
SELECT col_default_is('audit_events', 'occurred_at', 'now()');

-- ---------------------------------------------------------------------------
-- Foreign key: actor_user_id -> users(id) ON DELETE SET NULL.
-- pgTAP doesn't ship a helper for ON DELETE actions, so we inspect pg_constraint
-- directly. confdeltype = 'n' is "SET NULL" in PostgreSQL's catalog encoding.
-- ---------------------------------------------------------------------------
SELECT is(
    (SELECT confdeltype
       FROM pg_constraint
      WHERE conrelid = 'audit_events'::regclass
        AND contype  = 'f'
        AND conkey   = ARRAY[
            (SELECT attnum
               FROM pg_attribute
              WHERE attrelid = 'audit_events'::regclass
                AND attname  = 'actor_user_id')
        ]),
    'n'::"char",
    'audit_events.actor_user_id FK to users(id) uses ON DELETE SET NULL'
);

-- ---------------------------------------------------------------------------
-- Indexes
-- ---------------------------------------------------------------------------
SELECT has_index('audit_events', 'ix_audit_events_actor_occurred');
SELECT has_index('audit_events', 'ix_audit_events_event_type_occurred');
SELECT has_index('audit_events', 'ix_audit_events_occurred_at');

SELECT index_is_partial(
    'audit_events',
    'ix_audit_events_actor_occurred',
    'ix_audit_events_actor_occurred is a partial index (WHERE actor_user_id IS NOT NULL)'
);

-- ---------------------------------------------------------------------------
-- Functional check: ON DELETE SET NULL behaviour. Wrap in a SAVEPOINT so the
-- inserted rows do not leak into other assertions and so we can roll back to a
-- known clean state before the outer ROLLBACK.
-- ---------------------------------------------------------------------------
SAVEPOINT fk_set_null_check;

WITH inserted_user AS (
    INSERT INTO users
        (email, normalized_email, security_stamp, concurrency_stamp, created_at, updated_at)
    VALUES
        ('audit-fk@example.com', 'AUDIT-FK@EXAMPLE.COM',
         'stamp-audit', 'concurrency-audit', now(), now())
    RETURNING id
)
INSERT INTO audit_events (actor_user_id, event_type, payload)
SELECT id, 'login.success', '{"src":"pgtap"}'::jsonb
  FROM inserted_user;

SELECT is(
    (SELECT count(*)::int FROM audit_events WHERE event_type = 'login.success'),
    1,
    'audit_events row inserted referencing the parent user'
);

SELECT is(
    (SELECT actor_user_id IS NOT NULL
       FROM audit_events
      WHERE event_type = 'login.success'),
    true,
    'actor_user_id is populated before parent delete'
);

DELETE FROM users WHERE email = 'audit-fk@example.com';

SELECT is(
    (SELECT actor_user_id
       FROM audit_events
      WHERE event_type = 'login.success'),
    NULL::uuid,
    'actor_user_id is NULL after parent user is deleted (ON DELETE SET NULL)'
);

ROLLBACK TO SAVEPOINT fk_set_null_check;

SELECT * FROM finish();

ROLLBACK;
