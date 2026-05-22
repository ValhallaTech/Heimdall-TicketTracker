namespace Heimdall.Core.Auditing;

/// <summary>
/// Dotted, dot-separated audit-event-type constants (Phase 4 convention; matches the
/// inline strings used in <c>AccountEndpoints</c> for <c>mfa.*</c> events). Defined in
/// <see cref="Heimdall.Core.Auditing"/> so producers (BLL services, Web endpoints, and
/// the DB-side audit triggers from <c>M202605130001_CreateSigningKeys</c>) all reference
/// a single source of truth.
/// </summary>
/// <remarks>
/// <para>
/// The five <c>token.signing_key.*</c> constants mirror exactly the <c>event_type</c>
/// values emitted by the <c>trg_signing_keys_audit</c> trigger installed in
/// <see cref="Heimdall.DAL.Migrations.M202605130001_CreateSigningKeys"/> and listed in
/// <c>docs/proposals/phase-5-signing-key-hardening.md</c> §2.4.
/// </para>
/// <para>
/// Phase 5.3 step 8 adds the six <c>token.access.*</c> / <c>token.refresh.*</c>
/// constants emitted by the (Phase 5.4) <c>ApiAuthEndpoints</c> token / refresh
/// handlers and the (Phase 5.5) logout / denylist paths. Payload shapes for each are
/// documented inline below and pinned by the step-21 acceptance test.
/// </para>
/// </remarks>
public static class AuditEventTypes
{
    /// <summary>
    /// A new signing key pair has been created and inserted into <c>signing_keys</c>
    /// (the <c>signing_keys_insert</c> SECURITY DEFINER function fires the
    /// <c>AFTER INSERT</c> trigger).
    /// </summary>
    public const string TokenSigningKeyGenerated = "token.signing_key.generated";

    /// <summary>
    /// An existing signing key has been mutated such that a new key now overlaps with
    /// it — emitted by the trigger when an <c>UPDATE</c> touches a row outside the
    /// retirement / revocation branches.
    /// </summary>
    public const string TokenSigningKeyRotated = "token.signing_key.rotated";

    /// <summary>
    /// A signing key has been retired via <c>SigningKeyService.RetireAsync</c> —
    /// the trigger sets this event type when <c>retired_at</c> transitions from
    /// <c>NULL</c> to <c>NOT NULL</c>. Tokens already in flight remain verifiable
    /// until <c>not_after</c>.
    /// </summary>
    public const string TokenSigningKeyRetired = "token.signing_key.retired";

    /// <summary>
    /// Emergency revocation — <c>not_after</c> has been forced to a value
    /// <c>&lt;= now()</c>. Distinguished from rotation by the trigger logic in
    /// <c>signing_keys_audit_trg</c>.
    /// </summary>
    public const string TokenSigningKeyRevoked = "token.signing_key.revoked";

    /// <summary>
    /// A signing-key row has been deleted (typically by the scheduled sweeper after
    /// <c>not_after</c> has long passed). Emitted by the <c>AFTER DELETE</c> branch
    /// of the trigger with the <c>OLD</c> row's payload.
    /// </summary>
    public const string TokenSigningKeyExpired = "token.signing_key.expired";

    // ---- Phase 5.3 step 8 — token lifecycle (issue / rotate / revoke) ----------

    /// <summary>
    /// An access token (JWT) has been minted by <c>JwtTokenIssuer</c> and returned to
    /// the client. Emitted on both the password-grant (Phase 5.4 step 9) and the
    /// refresh-rotation (Phase 5.4 step 10) success paths. Payload:
    /// <c>{ "jti": "&lt;guid-n&gt;", "user_id": "&lt;guid&gt;", "amr": ["pwd","mfa"?] }</c>.
    /// </summary>
    public const string TokenAccessIssued = "token.access.issued";

    /// <summary>
    /// A refresh token has been minted and persisted to <c>refresh_tokens</c>. Emitted
    /// on the password-grant success path (Phase 5.4 step 9) — refresh-rotation uses
    /// <see cref="TokenRefreshRotated"/> instead so the audit trail distinguishes the
    /// family-root row from rotated successors. Payload:
    /// <c>{ "jti": "&lt;refresh-token-id&gt;", "family_id": "&lt;guid&gt;", "user_id": "&lt;guid&gt;" }</c>.
    /// </summary>
    public const string TokenRefreshIssued = "token.refresh.issued";

    /// <summary>
    /// A refresh token has been rotated for a successor row inside a single
    /// transaction. Emitted by the Phase 5.4 step 10 happy-path handler after
    /// <c>IRefreshTokenRepository.RotateAsync</c> returns <c>true</c>. Payload:
    /// <c>{ "jti": "&lt;new-id&gt;", "parent_id": "&lt;old-id&gt;", "family_id": "&lt;guid&gt;" }</c>.
    /// </summary>
    public const string TokenRefreshRotated = "token.refresh.rotated";

    /// <summary>
    /// A previously-rotated refresh token has been presented again — the Phase 5.4
    /// step 10 family-replay detector swept the entire family via
    /// <c>RevokeFamilyAsync(..., RefreshTokenRevokedReason.FamilyReplay)</c>. Payload:
    /// <c>{ "jti": "&lt;presented-id&gt;", "family_id": "&lt;guid&gt;" }</c>. This is the
    /// security-critical event — alerting hooks should treat it as high-signal.
    /// </summary>
    public const string TokenRefreshReplayed = "token.refresh.replayed";

    /// <summary>
    /// An access token's <c>jti</c> has been added to the Phase 5.5 Redis denylist
    /// (logout or admin revoke). Payload:
    /// <c>{ "jti": "&lt;guid-n&gt;", "reason": "logout" | "admin_revoke" }</c>.
    /// </summary>
    public const string TokenAccessRevoked = "token.access.revoked";

    /// <summary>
    /// A refresh-token family has been bulk-revoked via
    /// <c>IRefreshTokenRepository.RevokeFamilyAsync</c> — distinct from
    /// <see cref="TokenRefreshReplayed"/> because logout and admin-revoke both write
    /// this without an accompanying replay signal. Payload:
    /// <c>{ "family_id": "&lt;guid&gt;", "reason": "logout" | "admin_revoke" | "family_replay" }</c>.
    /// </summary>
    public const string TokenRefreshFamilyRevoked = "token.refresh.family_revoked";

    /// <summary>
    /// The Phase 5.5 Redis access-token denylist was unreachable (connection /
    /// timeout failure) when <c>JwtBearerEvents.OnTokenValidated</c> tried to
    /// look up a presented <c>jti</c>. Written by the Phase 5.5 step 12 fail-open
    /// branch (non-admin reads) so the SOC has a positive trace of "we let this
    /// request through despite an outage." Admin-policy-gated endpoints fail
    /// closed and do not write this event. Payload:
    /// <c>{ "jti": "&lt;guid-n&gt;", "user_id": "&lt;guid&gt;" }</c>.
    /// </summary>
    public const string TokenAccessDenylistUnavailable = "token.access.denylist_unavailable";
}
