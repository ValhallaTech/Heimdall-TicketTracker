using System;

namespace Heimdall.Core.Models;

/// <summary>
/// Domain entity representing an authenticated user of the Heimdall application. The
/// columns mirror those required by ASP.NET Core Identity's user store (email +
/// normalized email, password hash, security / concurrency stamps, lockout fields)
/// plus a <see cref="SystemAdmin"/> flag and the standard audit timestamps. This type
/// deliberately has no dependency on <c>Microsoft.AspNetCore.Identity</c> so that
/// <c>Heimdall.Core</c> stays free of framework references — the Identity store
/// adapter lives in <c>Heimdall.DAL</c>.
/// </summary>
public class HeimdallUser
{
    /// <summary>Gets or sets the unique identifier (Postgres <c>uuid</c>).</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the user's email address — also used as the Identity user name.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Gets or sets the normalized (upper-cased) email used for case-insensitive lookups.</summary>
    public string NormalizedEmail { get; set; } = string.Empty;

    /// <summary>Gets or sets the hashed password produced by the Identity password hasher. <c>null</c> for external-only accounts.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>Gets or sets the security stamp used by Identity to invalidate persisted security state when credentials change.</summary>
    public string SecurityStamp { get; set; } = string.Empty;

    /// <summary>Gets or sets the optimistic-concurrency token. Bumped by the user store on every update.</summary>
    public string ConcurrencyStamp { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the user has confirmed their email address.</summary>
    public bool EmailConfirmed { get; set; }

    /// <summary>Gets or sets the UTC offset at which the current lockout ends, or <c>null</c> when not locked out.</summary>
    public DateTimeOffset? LockoutEnd { get; set; }

    /// <summary>Gets or sets a value indicating whether lockout is enabled for this user. Defaults to <c>true</c>.</summary>
    public bool LockoutEnabled { get; set; } = true;

    /// <summary>Gets or sets the number of consecutive failed access attempts since the last successful sign-in.</summary>
    public int AccessFailedCount { get; set; }

    /// <summary>Gets or sets a value indicating whether the user has the system administrator privilege.</summary>
    public bool SystemAdmin { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether two-factor authentication is enabled for this
    /// user. Defaults to <c>false</c>. Persisted to the <c>users.two_factor_enabled</c> column
    /// by <c>HeimdallUserStore</c> (Phase 4.1 step 1 of
    /// <c>docs/proposals/security-and-authorization.md</c> §9.3).
    /// </summary>
    public bool TwoFactorEnabled { get; set; }

    /// <summary>Gets or sets the UTC offset timestamp at which the user record was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the UTC offset timestamp at which the user record was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
