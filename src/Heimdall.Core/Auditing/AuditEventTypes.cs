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
/// Phase 5.1 (this PR) defines only the five <c>token.signing_key.*</c> constants —
/// these mirror exactly the <c>event_type</c> values emitted by the
/// <c>trg_signing_keys_audit</c> trigger installed in
/// <see cref="Heimdall.DAL.Migrations.M202605130001_CreateSigningKeys"/> and listed in
/// <c>docs/proposals/phase-5-signing-key-hardening.md</c> §2.4.
/// </para>
/// <para>
/// Phase 5 step 8 (a later PR) will extend this class with the remaining <c>token.*</c>
/// constants (<c>token.access.issued</c>, <c>token.refresh.issued</c>,
/// <c>token.refresh.rotated</c>, <c>token.refresh.replayed</c>,
/// <c>token.access.revoked</c>, <c>token.refresh.family_revoked</c>). The current scope
/// is the Phase 5.1 sub-set only; do not pre-add those constants here.
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
}
