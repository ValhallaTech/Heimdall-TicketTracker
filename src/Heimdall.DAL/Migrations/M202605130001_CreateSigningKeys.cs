using FluentMigrator;

namespace Heimdall.DAL.Migrations;

/// <summary>
/// Creates the <c>signing_keys</c> table and its hardened access surface for Phase 5.1
/// step 1 of <c>docs/proposals/security-and-authorization.md</c> §5.1 / §5.4, layered
/// with the controls mandated by <c>docs/proposals/phase-5-signing-key-hardening.md</c>
/// §2.1, §2.2 and §2.4. One atomic migration lands the table, supporting indexes, the
/// two Postgres roles, column-level grants, Row Level Security, the
/// <c>SECURITY DEFINER</c> reader/insert helpers, and the
/// <c>AFTER INSERT/UPDATE/DELETE</c> audit trigger — so deploys are either fully
/// hardened or rolled back. The audit trigger writes
/// <c>token.signing_key.&#42;</c> rows into <c>audit_events</c> so out-of-band
/// row manipulation (not just orchestrated rotation) is recorded.
/// </summary>
[Migration(202605130001, "Create signing_keys with RLS, two-role access, and audit trigger")]
public class M202605130001_CreateSigningKeys : Migration
{
    /// <summary>
    /// Creates the <c>signing_keys</c> table, the
    /// <c>ix_signing_keys_not_after_active</c> / <c>ix_signing_keys_validity_window</c>
    /// indexes, the <c>heimdall_app</c> and <c>heimdall_signer</c> roles with the
    /// column-scoped grant pattern from hardening §2.2, enables (and forces) Row Level
    /// Security with defence-in-depth permissive policies, installs the
    /// <c>signing_keys_insert</c> and <c>signing_keys_read_private</c>
    /// <c>SECURITY DEFINER</c> helpers, and attaches the audit trigger.
    /// </summary>
    public override void Up()
    {
        // -------------------------------------------------------------------
        // 1) Table.
        // private_key_protected is bytea (hardening §2.1) so the binary nature
        // of the ASP.NET Core Data Protection ciphertext is explicit and we
        // cannot accidentally introduce encoding bugs. The application MUST
        // wrap the PKCS#8 private half via the purpose-isolated protector
        // "Heimdall.JwtSigningKeys.v1" before INSERT.
        // -------------------------------------------------------------------
        Execute.Sql(@"
CREATE TABLE signing_keys (
    kid                    text        PRIMARY KEY,
    alg                    text        NOT NULL CHECK (alg IN ('RS256', 'ES256')),
    public_jwk             jsonb       NOT NULL,
    private_key_protected  bytea       NOT NULL,
    created_at             timestamptz NOT NULL DEFAULT now(),
    not_before             timestamptz NOT NULL,
    not_after              timestamptz NOT NULL,
    retired_at             timestamptz NULL
);

COMMENT ON COLUMN signing_keys.private_key_protected IS
    'PKCS#8 DER bytes wrapped by ASP.NET Core Data Protection under the purpose ""Heimdall.JwtSigningKeys.v1"". Never plaintext. Read only via signing_keys_read_private(kid); written only via signing_keys_insert(...). Column-level GRANT withholds direct SELECT/INSERT/UPDATE from heimdall_app — see role/grant block below.';
");

        // -------------------------------------------------------------------
        // 2) Indexes.
        //    a) Partial index for the signing-key-selection hot path
        //       (GetCurrentSigningKeyAsync / GetTrustedKeysAsync filter on
        //       retired_at IS NULL and order by not_after).
        //    b) Composite (not_before, not_after) for JWKS publication scans.
        // -------------------------------------------------------------------
        Execute.Sql(
            "CREATE INDEX ix_signing_keys_not_after_active "
                + "ON signing_keys (not_after) "
                + "WHERE retired_at IS NULL;"
        );

        Execute.Sql(
            "CREATE INDEX ix_signing_keys_validity_window "
                + "ON signing_keys (not_before, not_after);"
        );

        // -------------------------------------------------------------------
        // 3) Roles.
        // Both roles are NOLOGIN unless they already exist with LOGIN — these
        // are *target* roles for GRANTs and SECURITY DEFINER ownership; the
        // operator runbook (Phase 5 step 22) decides which Render connection
        // string maps to which role. The DO blocks are idempotent guards
        // because PostgreSQL has no `CREATE ROLE IF NOT EXISTS`.
        // The Down() path intentionally does NOT DROP ROLE — the roles may
        // be shared with other databases on the same managed Postgres
        // cluster, so destruction is reserved for an operator action.
        // -------------------------------------------------------------------
        Execute.Sql(@"
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'heimdall_app') THEN
        CREATE ROLE heimdall_app NOLOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'heimdall_signer') THEN
        CREATE ROLE heimdall_signer NOLOGIN;
    END IF;
END
$$;
");

        // -------------------------------------------------------------------
        // 4) Column-level grants — the PRIMARY access control.
        // heimdall_app sees every column EXCEPT private_key_protected.
        // heimdall_app cannot INSERT or UPDATE that column directly either —
        // those columns are deliberately omitted from the column lists below
        // so the only path that writes ciphertext is the signing_keys_insert
        // SECURITY DEFINER function (granted EXECUTE further down).
        // DELETE is a row operation (no column-level form), and heimdall_app
        // gets DELETE on the whole row — retirement is typically a soft
        // delete (UPDATE retired_at = now()), but operators may purge
        // long-expired rows.
        // heimdall_signer holds full SELECT on the row so the SECURITY
        // DEFINER reader function (owned by heimdall_signer) can return the
        // private ciphertext.
        // -------------------------------------------------------------------
        Execute.Sql(@"
GRANT SELECT (kid, alg, public_jwk, created_at, not_before, not_after, retired_at)
    ON signing_keys TO heimdall_app;
GRANT INSERT (kid, alg, public_jwk, created_at, not_before, not_after, retired_at)
    ON signing_keys TO heimdall_app;
GRANT UPDATE (kid, alg, public_jwk, created_at, not_before, not_after, retired_at)
    ON signing_keys TO heimdall_app;
GRANT DELETE ON signing_keys TO heimdall_app;

GRANT SELECT ON signing_keys TO heimdall_signer;
");

        // -------------------------------------------------------------------
        // 5) Row Level Security.
        // RLS is row-level, not column-level — it does NOT, by itself, hide
        // private_key_protected from heimdall_app. The column-level GRANT
        // above is the actual control. RLS is enabled here as DEFENCE IN
        // DEPTH: it ensures any future policy refinement (e.g. tenant
        // scoping) has a fail-closed substrate, and FORCE ROW LEVEL
        // SECURITY makes the table owner subject to policies too — closing
        // the common managed-Postgres footgun where the migrations role
        // happens to also own the table.
        // The two permissive SELECT policies grant *row visibility* to
        // each role; column visibility is still controlled by the GRANTs.
        // -------------------------------------------------------------------
        Execute.Sql(@"
ALTER TABLE signing_keys ENABLE ROW LEVEL SECURITY;
ALTER TABLE signing_keys FORCE ROW LEVEL SECURITY;

CREATE POLICY signing_key_signer_full
    ON signing_keys
    FOR SELECT
    TO heimdall_signer
    USING (true);

CREATE POLICY signing_key_app_read
    ON signing_keys
    FOR SELECT
    TO heimdall_app
    USING (true);

CREATE POLICY signing_key_app_write
    ON signing_keys
    FOR ALL
    TO heimdall_app
    USING (true)
    WITH CHECK (true);
");

        // -------------------------------------------------------------------
        // 6) SECURITY DEFINER helpers.
        // Hardening §2.2 documents these as the chosen alternative to
        // shipping two separate connection strings to Render — the
        // heimdall_app connection calls into functions owned by
        // heimdall_signer to read or write the private ciphertext.
        // The fixed search_path neutralises function-resolution injection.
        // -------------------------------------------------------------------
        Execute.Sql(@"
CREATE OR REPLACE FUNCTION signing_keys_read_private(p_kid text)
    RETURNS bytea
    LANGUAGE sql
    SECURITY DEFINER
    SET search_path = pg_catalog, public
AS $$
    SELECT private_key_protected FROM signing_keys WHERE kid = p_kid;
$$;

ALTER FUNCTION signing_keys_read_private(text) OWNER TO heimdall_signer;
REVOKE ALL ON FUNCTION signing_keys_read_private(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION signing_keys_read_private(text) TO heimdall_app;

CREATE OR REPLACE FUNCTION signing_keys_insert(
    p_kid                   text,
    p_alg                   text,
    p_public_jwk            jsonb,
    p_private_key_protected bytea,
    p_not_before            timestamptz,
    p_not_after             timestamptz
)
    RETURNS void
    LANGUAGE sql
    SECURITY DEFINER
    SET search_path = pg_catalog, public
AS $$
    INSERT INTO signing_keys (kid, alg, public_jwk, private_key_protected, not_before, not_after)
    VALUES (p_kid, p_alg, p_public_jwk, p_private_key_protected, p_not_before, p_not_after);
$$;

ALTER FUNCTION signing_keys_insert(text, text, jsonb, bytea, timestamptz, timestamptz)
    OWNER TO heimdall_signer;
REVOKE ALL ON FUNCTION signing_keys_insert(text, text, jsonb, bytea, timestamptz, timestamptz)
    FROM PUBLIC;
GRANT EXECUTE ON FUNCTION signing_keys_insert(text, text, jsonb, bytea, timestamptz, timestamptz)
    TO heimdall_app;
");

        // -------------------------------------------------------------------
        // 7) Audit trigger.
        // Writes one audit_events row per state change. event_type values
        // match hardening §2.4 with the token.* prefix used by step 8.
        // actor_user_id is intentionally NULL — trigger-sourced rows have
        // no application-side actor context, and that NULL is the precise
        // signal operators correlate with "out-of-band manipulation" in
        // the step-22 runbook.
        // The trigger function uses the audit_events.payload jsonb column
        // (created in M202605050002_CreateAuditEvents).
        // -------------------------------------------------------------------
        Execute.Sql(@"
CREATE OR REPLACE FUNCTION signing_keys_audit_trg()
    RETURNS trigger
    LANGUAGE plpgsql
AS $$
DECLARE
    v_event_type text;
    v_row        signing_keys%ROWTYPE;
BEGIN
    IF (TG_OP = 'INSERT') THEN
        v_event_type := 'token.signing_key.generated';
        v_row := NEW;
    ELSIF (TG_OP = 'DELETE') THEN
        v_event_type := 'token.signing_key.expired';
        v_row := OLD;
    ELSIF (TG_OP = 'UPDATE') THEN
        IF (OLD.retired_at IS NULL AND NEW.retired_at IS NOT NULL) THEN
            v_event_type := 'token.signing_key.retired';
        ELSIF (OLD.not_after IS DISTINCT FROM NEW.not_after AND NEW.not_after <= now()) THEN
            v_event_type := 'token.signing_key.revoked';
        ELSE
            v_event_type := 'token.signing_key.rotated';
        END IF;
        v_row := NEW;
    END IF;

    INSERT INTO audit_events (actor_user_id, event_type, target, payload, occurred_at)
    VALUES (
        NULL,
        v_event_type,
        v_row.kid,
        jsonb_build_object(
            'kid',        v_row.kid,
            'alg',        v_row.alg,
            'not_before', v_row.not_before,
            'not_after',  v_row.not_after,
            'retired_at', v_row.retired_at
        ),
        now()
    );

    RETURN NULL;
END;
$$;

CREATE TRIGGER trg_signing_keys_audit
    AFTER INSERT OR UPDATE OR DELETE ON signing_keys
    FOR EACH ROW
    EXECUTE FUNCTION signing_keys_audit_trg();
");
    }

    /// <summary>
    /// Drops the trigger, audit/reader/insert functions, RLS policies, role
    /// grants, indexes, and table in reverse order. Roles
    /// <c>heimdall_app</c> and <c>heimdall_signer</c> are intentionally
    /// <strong>not</strong> dropped — they may be shared with other databases
    /// on the same managed Postgres cluster (Render). Role lifecycle is an
    /// operator action documented in the Phase 5 step 22 runbook.
    /// </summary>
    public override void Down()
    {
        Execute.Sql(@"
DROP TRIGGER IF EXISTS trg_signing_keys_audit ON signing_keys;
DROP FUNCTION IF EXISTS signing_keys_audit_trg();
DROP FUNCTION IF EXISTS signing_keys_insert(text, text, jsonb, bytea, timestamptz, timestamptz);
DROP FUNCTION IF EXISTS signing_keys_read_private(text);

DROP POLICY IF EXISTS signing_key_app_write  ON signing_keys;
DROP POLICY IF EXISTS signing_key_app_read   ON signing_keys;
DROP POLICY IF EXISTS signing_key_signer_full ON signing_keys;
");

        // Revoke before DROP TABLE so that if the table is recreated later
        // by re-running Up() we know we start from a clean grant slate.
        Execute.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'heimdall_app') THEN
        REVOKE ALL ON signing_keys FROM heimdall_app;
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'heimdall_signer') THEN
        REVOKE ALL ON signing_keys FROM heimdall_signer;
    END IF;
END
$$;
");

        Execute.Sql("DROP TABLE IF EXISTS signing_keys;");
    }
}
