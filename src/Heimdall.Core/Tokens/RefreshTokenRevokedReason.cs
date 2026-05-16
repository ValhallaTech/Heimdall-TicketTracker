namespace Heimdall.Core.Tokens;

/// <summary>
/// Allowed values for <c>refresh_tokens.revoked_reason</c>. These strings are the
/// exact set permitted by the <c>CHECK</c> constraint installed by
/// <c>M202605200001_CreateRefreshTokens</c>; any other value sent to Postgres
/// would surface as an opaque check-constraint violation. The
/// <c>IRefreshTokenRepository.RevokeFamilyAsync</c> implementation validates
/// against this allow-list before binding the parameter — never let an
/// arbitrary string reach the database.
/// </summary>
public static class RefreshTokenRevokedReason
{
    /// <summary>The token was revoked because it was rotated for a successor row.</summary>
    public const string Rotated = "rotated";

    /// <summary>The user logged out and the token (and its family) were revoked.</summary>
    public const string Logout = "logout";

    /// <summary>
    /// A previously-rotated token was presented again — the step-10 detector swept
    /// the whole family.
    /// </summary>
    public const string FamilyReplay = "family_replay";

    /// <summary>An administrator revoked the token (or the family) out-of-band.</summary>
    public const string AdminRevoke = "admin_revoke";
}
