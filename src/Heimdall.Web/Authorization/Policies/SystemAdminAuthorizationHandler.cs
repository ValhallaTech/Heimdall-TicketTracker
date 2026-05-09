using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Heimdall.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;

namespace Heimdall.Web.Authorization.Policies;

/// <summary>
/// Authorization handler for <see cref="SystemAdminRequirement"/>. Resolves the
/// actor's user id from <see cref="ClaimTypes.NameIdentifier"/> and consults
/// <see cref="IUserLookup.IsSystemAdminAsync"/>; succeeds only on a parseable
/// id whose <c>users.system_admin</c> column is <c>true</c>.
/// </summary>
/// <remarks>
/// Deliberately does <strong>not</strong> depend on
/// <c>IOpenFgaAuthorizationService</c> — see
/// <c>docs/proposals/openfga.md</c> §3 step 10 (DB-only break-glass authority).
/// Deny-closed on every failure path: missing claim, unparseable id, lookup
/// failure → <c>context.Fail()</c> is implicit (no <c>Succeed</c> call).
/// </remarks>
public sealed class SystemAdminAuthorizationHandler : AuthorizationHandler<SystemAdminRequirement>
{
    private readonly IUserLookup _userLookup;
    private readonly ILogger<SystemAdminAuthorizationHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemAdminAuthorizationHandler"/> class.
    /// </summary>
    /// <param name="userLookup">Sidecar-free <c>system_admin</c> lookup.</param>
    /// <param name="logger">Structured logger.</param>
    /// <exception cref="ArgumentNullException">If any argument is <c>null</c>.</exception>
    public SystemAdminAuthorizationHandler(
        IUserLookup userLookup,
        ILogger<SystemAdminAuthorizationHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(userLookup);
        ArgumentNullException.ThrowIfNull(logger);

        _userLookup = userLookup;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        SystemAdminRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        string? raw = context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(raw, out Guid actorId))
        {
            // Deny-closed: no actor id → no decision can be made.
            return;
        }

        try
        {
            bool isAdmin = await _userLookup.IsSystemAdminAsync(actorId).ConfigureAwait(false);
            if (isAdmin)
            {
                context.Succeed(requirement);
            }
        }
        catch (OperationCanceledException)
        {
            // Cooperative cancellation: surface to the policy pipeline.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Deny-closed: SystemAdmin lookup failed for actor {ActorId}.",
                actorId);
        }
    }
}
