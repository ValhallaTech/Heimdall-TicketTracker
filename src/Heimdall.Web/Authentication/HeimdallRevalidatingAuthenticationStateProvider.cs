using System;
using System.Globalization;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Models;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Heimdall.Web.Authentication;

/// <summary>
/// Per-circuit Blazor Server authentication-state provider that periodically revalidates
/// the captured <see cref="ClaimsPrincipal"/> against the <c>users</c> table by comparing
/// the principal's <c>SecurityStamp</c> claim with the value stored in the database.
/// </summary>
/// <remarks>
/// <para>
/// Implements Phase 1 step 5 of <c>docs/proposals/security-and-authorization.md</c> §3.4 /
/// §9.3. Blazor Server captures the user's identity into the SignalR circuit on connect;
/// without revalidation, an admin disabling a user, forcing a sign-out, or a user
/// resetting their password would not take effect until the user happened to navigate to
/// a new page or reconnect. This provider closes that gap by polling the
/// <c>security_stamp</c> column on a fixed cadence and invalidating the circuit when the
/// stored stamp differs from the principal's claim.
/// </para>
/// <para>
/// Failure handling is intentionally fail-secure: any unexpected exception thrown by the
/// underlying <see cref="IUserStore{TUser}"/> is logged and treated as an invalid state.
/// <see cref="OperationCanceledException"/> is the only exception allowed to propagate so
/// circuit teardown / shutdown semantics work as expected.
/// </para>
/// </remarks>
public sealed class HeimdallRevalidatingAuthenticationStateProvider
    : RevalidatingServerAuthenticationStateProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<IdentityOptions> _identityOptions;
    private readonly ILogger<HeimdallRevalidatingAuthenticationStateProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="HeimdallRevalidatingAuthenticationStateProvider"/> class.
    /// </summary>
    /// <param name="loggerFactory">Logger factory forwarded to the base type.</param>
    /// <param name="scopeFactory">
    /// Scope factory used to resolve a scoped <see cref="IUserStore{TUser}"/> per
    /// revalidation tick (the user store opens its own short-lived DB connections).
    /// </param>
    /// <param name="identityOptions">
    /// Identity options snapshot — read once per revalidation to obtain the configured
    /// security-stamp claim type rather than hard-coding the framework default.
    /// </param>
    public HeimdallRevalidatingAuthenticationStateProvider(
        ILoggerFactory loggerFactory,
        IServiceScopeFactory scopeFactory,
        IOptions<IdentityOptions> identityOptions)
        : base(loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(identityOptions);

        _scopeFactory = scopeFactory;
        _identityOptions = identityOptions;

        // The base type captures its own ILogger privately and does not expose it.
        // Build a separately-categorized logger off the same factory so our
        // revalidation-failure messages are auditable under this concrete category.
        _logger = loggerFactory.CreateLogger<HeimdallRevalidatingAuthenticationStateProvider>();
    }

    /// <summary>
    /// Gets the interval between revalidation polls. Five minutes is short enough that
    /// security-stamp changes (password reset, force-logout, account disable) propagate
    /// to live circuits within ~5 min, but long enough not to hammer the database on
    /// every long-lived circuit. The proposal §3.4 calls for a "short revalidation
    /// interval"; this is that interval.
    /// </summary>
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(5);

    /// <summary>
    /// Validates the supplied authentication state by comparing the principal's
    /// security-stamp claim with the value currently stored on the user record.
    /// </summary>
    /// <param name="authenticationState">Captured circuit authentication state.</param>
    /// <param name="cancellationToken">Cancellation token for circuit teardown.</param>
    /// <returns>
    /// <see langword="true"/> when the principal is anonymous or its security stamp
    /// matches the stored value; otherwise <see langword="false"/> (which causes the
    /// base provider to invalidate the circuit's auth state).
    /// </returns>
    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authenticationState);

        var principal = authenticationState.User;
        if (principal?.Identity is null || !principal.Identity.IsAuthenticated)
        {
            // Anonymous state is always "valid" — there is nothing to revalidate.
            return true;
        }

        var idString = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(idString) ||
            !Guid.TryParse(idString, CultureInfo.InvariantCulture, out _))
        {
            _logger.LogWarning(
                "Revalidation failed: principal is missing a valid {ClaimType} claim.",
                ClaimTypes.NameIdentifier);
            return false;
        }

        var stampClaimType = _identityOptions.Value.ClaimsIdentity.SecurityStampClaimType;
        var principalStamp = principal.FindFirstValue(stampClaimType);
        if (string.IsNullOrEmpty(principalStamp))
        {
            _logger.LogWarning(
                "Revalidation failed: principal {UserId} is missing the {ClaimType} claim.",
                idString,
                stampClaimType);
            return false;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var userStore = scope.ServiceProvider.GetRequiredService<IUserStore<HeimdallUser>>();

            var user = await userStore
                .FindByIdAsync(idString, cancellationToken)
                .ConfigureAwait(false);

            if (user is null)
            {
                _logger.LogWarning(
                    "Revalidation failed: user {UserId} no longer exists.",
                    idString);
                return false;
            }

            if (!string.Equals(user.SecurityStamp, principalStamp, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Revalidation invalidated circuit for user {UserId}: security stamp changed.",
                    idString);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            // Cancellation is part of normal circuit teardown — never swallow it.
            throw;
        }
        catch (Exception ex)
        {
            // Fail-secure: any unexpected failure invalidates the circuit so a
            // transient DB outage cannot extend a session past a security event.
            _logger.LogWarning(
                ex,
                "Revalidation failed for user {UserId} due to an unexpected exception; invalidating circuit.",
                idString);
            return false;
        }
    }
}
