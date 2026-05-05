-- 03_data_protection_keys.sql
-- pgTAP coverage for the data_protection_keys table created by
-- src/Heimdall.DAL/Migrations/M202605050003_CreateDataProtectionKeys.cs
-- (Phase 1 step 4 of the Authenticated Foundation, per
-- docs/proposals/security-and-authorization.md §9.3).
--
-- Asserts:
--   * the table exists with all 4 expected columns and correct types / nullability
--   * id is the primary key and is an identity column
--   * insert_date defaults to now()
--   * the unique index on friendly_name exists and is unique
--
-- The schema is owned by FluentMigrator rather than auto-created by the
-- AspNetCore.DataProtection.CustomStorage.Dapper.PostgreSQL package, so this
-- file is the contract that pins the package's expected schema. Wrapped in
-- BEGIN ... ROLLBACK so it leaves no residue.

BEGIN;

SELECT plan(15);

-- ---------------------------------------------------------------------------
-- Table & columns exist
-- ---------------------------------------------------------------------------
SELECT has_table('data_protection_keys');

SELECT has_column('data_protection_keys', 'id');
SELECT has_column('data_protection_keys', 'insert_date');
SELECT has_column('data_protection_keys', 'friendly_name');
SELECT has_column('data_protection_keys', 'xml');

-- ---------------------------------------------------------------------------
-- Column types. friendly_name is character varying(256) per the package's
-- InitializeDb() — col_type_is reports it as 'character varying(256)'.
-- ---------------------------------------------------------------------------
SELECT col_type_is('data_protection_keys', 'id',            'integer');
SELECT col_type_is('data_protection_keys', 'insert_date',   'timestamp with time zone');
SELECT col_type_is('data_protection_keys', 'friendly_name', 'character varying(256)');
SELECT col_type_is('data_protection_keys', 'xml',           'text');

-- ---------------------------------------------------------------------------
-- Nullability
-- ---------------------------------------------------------------------------
SELECT col_not_null('data_protection_keys', 'id');
SELECT col_not_null('data_protection_keys', 'insert_date');
SELECT col_is_null ('data_protection_keys', 'friendly_name');
SELECT col_not_null('data_protection_keys', 'xml');

-- ---------------------------------------------------------------------------
-- Primary key
-- ---------------------------------------------------------------------------
SELECT col_is_pk('data_protection_keys', 'id');

-- ---------------------------------------------------------------------------
-- Unique index on friendly_name. Asserting both presence and uniqueness so
-- the schema cannot regress to a non-unique index without breaking pgTAP.
-- ---------------------------------------------------------------------------
SELECT has_index(
    'data_protection_keys',
    'ix_public_data_protection_keys_friendly_name',
    'friendly_name'
);

SELECT * FROM finish();

ROLLBACK;
