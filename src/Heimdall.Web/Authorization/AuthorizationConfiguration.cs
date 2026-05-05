using System;
using Microsoft.AspNetCore.Authorization;

namespace Heimdall.Web.Authorization;

/// <summary>
/// Centralised authorization wiring for Heimdall (Phase 1 step 9 of
/// <c>docs/proposals/security-and-authorization.md</c> §9.3). Hosts the
/// configuration delegate passed to
/// <c>IServiceCollection.AddAuthorization(...)</c> so production startup and
/// the focused unit tests in <c>Heimdall.Web.Tests.Authorization</c> apply
/// the exact same options.
/// </summary>
public static class AuthorizationConfiguration
{
    /// <summary>
    /// Applies Heimdall's global authorization options onto the supplied
    /// <see cref="AuthorizationOptions"/> instance. Currently sets a fallback
    /// policy that requires an authenticated user — every endpoint without
    /// its own authorization metadata is therefore denied to anonymous
    /// callers and explicitly-public endpoints must opt out with
    /// <c>[AllowAnonymous]</c> (login, logout, access-denied, the static
    /// error / not-found pages, the splash page).
    /// </summary>
    /// <remarks>
    /// This is intentionally coarse — Phase 3 will layer OpenFGA-backed
    /// resource-level checks on top. Until then "must be signed in" is the
    /// only org-wide check.
    /// </remarks>
    /// <param name="options">The options instance to mutate.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> is <c>null</c>.
    /// </exception>
    public static void Configure(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    }
}
