using System;
using System.Threading.Tasks;
using Heimdall.Web.Authorization.Policies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Heimdall.Web.Authorization;

/// <summary>
/// Custom <see cref="IAuthorizationMiddlewareResultHandler"/> that intercepts
/// failures specifically caused by an unsatisfied <see cref="RequireMfaRequirement"/>
/// and bounces the actor to <c>/account/mfa/setup</c> instead of letting the
/// default handler emit a 403 / access-denied response. Phase 4.6 step 17 of
/// <c>docs/implementation/phase-4-checklist.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a redirect, not a 403?</b> The Phase 4.6 step 16 handler succeeds for
/// non-admins, so a <see cref="RequireMfaRequirement"/> failure necessarily
/// describes an admin who is missing MFA. The right UX for that admin is "go
/// enrol now", not "you cannot view this page" — they own the page, they just
/// have not enrolled yet.
/// </para>
/// <para>
/// <b>Anti-loop short-circuit.</b> If the request is already targeting any
/// path under <c>/account/mfa</c>, the handler defers to the default — otherwise
/// an admin who could not enrol (e.g. a transient DB outage) would be redirected
/// back to <c>/account/mfa/setup</c> from <c>/account/mfa/setup</c> in a loop.
/// </para>
/// <para>
/// <b>Scope.</b> Only <see cref="RequireMfaRequirement"/> failures trigger the
/// redirect — every other policy failure (including ordinary
/// <see cref="OpenFgaRequirement"/> / <see cref="SystemAdminRequirement"/>
/// denials) flows to the default handler so the canonical
/// <c>/access-denied</c> page is preserved for those cases.
/// </para>
/// </remarks>
public sealed class MfaSetupRedirectMiddlewareResultHandler : IAuthorizationMiddlewareResultHandler
{
    /// <summary>The path admins missing MFA are redirected to.</summary>
    public const string MfaSetupPath = "/account/mfa/setup";

    /// <summary>Path prefix used by the anti-loop short-circuit.</summary>
    public const string MfaPathPrefix = "/account/mfa";

    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();
    private readonly ILogger<MfaSetupRedirectMiddlewareResultHandler> _logger;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="logger">Structured logger.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="logger"/> is <c>null</c>.</exception>
    public MfaSetupRedirectMiddlewareResultHandler(
        ILogger<MfaSetupRedirectMiddlewareResultHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        if (ShouldRedirectToMfaSetup(context, authorizeResult))
        {
            _logger.LogInformation(
                "Redirecting admin to MFA setup. UserId={UserId} OriginalPath={OriginalPath}",
                context.User?.Identity?.Name ?? "(unknown)",
                context.Request.Path.Value);

            context.Response.Redirect(MfaSetupPath);
            return;
        }

        await _defaultHandler.HandleAsync(next, context, policy, authorizeResult).ConfigureAwait(false);
    }

    private static bool ShouldRedirectToMfaSetup(
        HttpContext context,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Succeeded)
        {
            return false;
        }

        AuthorizationFailure? failure = authorizeResult.AuthorizationFailure;
        if (failure is null)
        {
            return false;
        }

        bool failedOnRequireMfa = false;
        foreach (IAuthorizationRequirement requirement in failure.FailedRequirements)
        {
            if (requirement is RequireMfaRequirement)
            {
                failedOnRequireMfa = true;
                break;
            }
        }

        if (!failedOnRequireMfa)
        {
            return false;
        }

        // Anti-loop: never redirect a request that is already aimed at the
        // MFA pages. StringComparison.OrdinalIgnoreCase mirrors ASP.NET path
        // matching semantics (URLs are case-insensitive on the routing side).
        PathString path = context.Request.Path;
        if (path.StartsWithSegments(MfaPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Only redirect for safe-method requests. Redirecting a POST would
        // silently drop the form payload; let the default 403 surface so the
        // client sees the failure cleanly.
        if (!HttpMethods.IsGet(context.Request.Method)
            && !HttpMethods.IsHead(context.Request.Method))
        {
            return false;
        }

        return true;
    }
}
