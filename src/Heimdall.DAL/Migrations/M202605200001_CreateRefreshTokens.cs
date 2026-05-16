using FluentMigrator;

namespace Heimdall.DAL.Migrations;

/// <summary>
/// Creates the <c>refresh_tokens</c> table for Phase 5.2 step 4 of
/// <c>docs/proposals/security-and-authorization.md</c> §5.1 / §5.2 and the matching
/// row in <c>docs/implementation/phase-5-checklist.md</c>. One row per refresh token;
/// rotation links members of a family through <c>parent_id</c> and <c>replaced_by</c>
/// so the step-10 family-replay detector can sweep an entire chain on the first
/// observed reuse of a previously-rotated token.
/// <para>
/// Unlike <c>signing_keys</c> (Phase 5.1, hardened with the two-role +
/// <c>SECURITY DEFINER</c> + RLS posture), <c>refresh_tokens</c> stores only the
/// PBKDF2 hash of each token — never the plaintext token and never any asymmetric
/// secret. The checklist therefore does not call for column-level GRANTs, RLS, or
/// a <c>SECURITY DEFINER</c> write path here. Access is controlled through the
/// normal application role: when the <c>heimdall_app</c> role exists (it is created
/// by <c>M202605130001_CreateSigningKeys</c>, but may be absent on databases that
/// have not yet reached that migration when this one runs in isolation), full
/// table-level CRUD is granted to it. Other databases on the cluster fall back to
/// the implicit migration-runner role.
/// </para>
/// </summary>
[Migration(202605200001, "Create refresh_tokens")]
public class M202605200001_CreateRefreshTokens : Migration
{
    /// <summary>
    /// Creates the <c>refresh_tokens</c> table with all ten columns, the two FKs
    /// back to <c>refresh_tokens(id)</c> for the rotation chain, the FK to
    /// <c>users(id)</c> with <c>ON DELETE CASCADE</c>, the <c>revoked_reason</c>
    /// CHECK constraint, the <c>(user_id, family_id)</c> composite index for the
    /// revoke-family hot path, and the partial <c>(expires_at) WHERE
    /// revoked_at IS NULL</c> index for the expired-but-not-revoked sweeper.
    /// </summary>
    public override void Up()
    {
        // -------------------------------------------------------------------
        // 1) Table.
        //    token_hash is text (the PBKDF2 string emitted by
        //    IPasswordHasher<HeimdallUser>) — never plaintext, never bytea.
        //    parent_id is NULL only on the family-root row; replaced_by is
        //    set during rotation by step 5's RotateAsync. Both are
        //    ON DELETE SET NULL so a manual purge of one row does not
        //    cascade-destroy the rest of the chain — the chain is reasoning
        //    metadata, not referential integrity for the user.
        // -------------------------------------------------------------------
        Execute.Sql(@"
CREATE TABLE refresh_tokens (
    id              uuid        PRIMARY KEY,
    user_id         uuid        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_hash      text        NOT NULL UNIQUE,
    family_id       uuid        NOT NULL,
    parent_id       uuid        NULL REFERENCES refresh_tokens(id) ON DELETE SET NULL,
    replaced_by     uuid        NULL REFERENCES refresh_tokens(id) ON DELETE SET NULL,
    issued_at       timestamptz NOT NULL DEFAULT now(),
    expires_at      timestamptz NOT NULL,
    revoked_at      timestamptz NULL,
    revoked_reason  text        NULL CHECK (revoked_reason IN ('rotated','logout','family_replay','admin_revoke'))
);");

        // -------------------------------------------------------------------
        // 2) Indexes.
        //    a) Composite (user_id, family_id) for RevokeFamilyAsync — the
        //       step-10 family-replay sweep is the hottest write path here.
        //    b) Partial (expires_at) WHERE revoked_at IS NULL for the
        //       periodic sweeper that purges expired-but-not-revoked rows
        //       (revoked rows are intentionally retained for audit and are
        //       outside the sweeper's responsibility).
        // -------------------------------------------------------------------
        Execute.Sql(
            "CREATE INDEX ix_refresh_tokens_user_family "
                + "ON refresh_tokens (user_id, family_id);"
        );

        Execute.Sql(
            "CREATE INDEX ix_refresh_tokens_expires_active "
                + "ON refresh_tokens (expires_at) "
                + "WHERE revoked_at IS NULL;"
        );

        // -------------------------------------------------------------------
        // 3) GRANTs — conditional on heimdall_app existing.
        //    The role is created by M202605130001_CreateSigningKeys, which
        //    runs before this migration in the standard sequence, but the
        //    DO $$ IF EXISTS $$ guard keeps the migration runnable in
        //    isolation (e.g. on a fresh test database where the Phase 5.1
        //    migration has been skipped, or against a cluster where roles
        //    are managed out-of-band). When the role is absent the table
        //    is reachable only via the implicit migration-runner role,
        //    which matches the posture of every other application table
        //    in the schema (tickets, users, organizations, …).
        // -------------------------------------------------------------------
        Execute.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'heimdall_app') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE ON refresh_tokens TO heimdall_app;
    END IF;
END
$$;
");
    }

    /// <summary>
    /// Drops the <c>refresh_tokens</c> table. Both supporting indexes are dropped
    /// implicitly by <c>DROP TABLE</c>. The <c>heimdall_app</c> role is intentionally
    /// not dropped — it may be shared with other databases on the cluster, and its
    /// lifecycle is the operator's responsibility per the step-22 runbook (same
    /// stance as <c>M202605130001_CreateSigningKeys.Down()</c>).
    /// </summary>
    public override void Down()
    {
        Execute.Sql("DROP TABLE IF EXISTS refresh_tokens;");
    }
}
