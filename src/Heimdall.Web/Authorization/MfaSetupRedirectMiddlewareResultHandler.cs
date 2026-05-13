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
            // Preserve the originally-requested path + query so the user lands
            // back where they were heading after completing enrolment.
            //
            // Defence-in-depth: `Request.Path` / `Request.QueryString` are
            // user-controlled. ASP.NET parses them into a PathString that
            // already starts with '/' for absolute-path requests, but we
            // re-validate here to keep CodeQL's open-redirect / log-injection
            // checkers happy and to avoid forwarding anything weird into the
            // returnUrl query parameter.
            string rawPath = context.Request.Path.Value ?? string.Empty;
            string rawQuery = context.Request.QueryString.Value ?? string.Empty;
            string originalPathAndQuery = $"{rawPath}{rawQuery}";

            string redirectTarget = IsSafeLocalReturnPath(originalPathAndQuery)
                ? $"{MfaSetupPath}?returnUrl={Uri.EscapeDataString(originalPathAndQuery)}"
                : MfaSetupPath;

            _logger.LogInformation(
                "Redirecting admin to MFA setup. UserId={UserId} OriginalPath={OriginalPath}",
                SanitizeForLog(context.User?.Identity?.Name) ?? "(unknown)",
                SanitizeForLog(originalPathAndQuery));

            context.Response.Redirect(redirectTarget);
            return;
        }

        await _defaultHandler.HandleAsync(next, context, policy, authorizeResult).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates that the candidate string is a same-origin local path. Matches
    /// the conservative shape used by the rest of <c>AccountEndpoints</c>:
    /// must be non-empty, start with a single '/', and not start with '//' or
    /// '/\' (which browsers can interpret as protocol-relative or
    /// network-path-reference URLs).
    /// </summary>
    private static bool IsSafeLocalReturnPath(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        if (candidate[0] != '/')
        {
            return false;
        }

        if (candidate.Length >= 2 && (candidate[1] == '/' || candidate[1] == '\\'))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Strips CR/LF (and other control characters that would forge new log
    /// lines) before emitting a user-controlled value through the structured
    /// logger. Returns <c>null</c> for <c>null</c> input so callers can keep
    /// their own null-coalescing semantics.
    /// </summary>
    private static string? SanitizeForLog(string? value)
    {
        if (value is null)
        {
            return null;
        }

        // Capping protects log buffers from arbitrarily long user input.
        const int MaxLogChars = 256;
        ReadOnlySpan<char> source =
            value.Length > MaxLogChars ? value.AsSpan(0, MaxLogChars) : value.AsSpan();

        Span<char> buffer = stackalloc char[source.Length];
        int written = 0;
        foreach (char c in source)
        {
            // Strip C0 control chars (including CR/LF) and DEL. Printable
            // characters in the safe range are kept verbatim.
            if (c >= 0x20 && c != 0x7F)
            {
                buffer[written++] = c;
            }
        }

        return new string(buffer[..written]);
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

        // Only redirect when the user is authenticated — an anonymous caller
        // belongs on /login, not /account/mfa/setup, and the default handler
        // will produce the correct 401/redirect-to-login result.
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        // Only redirect when RequireMfaRequirement is the *only* failed
        // requirement. If the user also failed e.g. SystemAdmin, sending them
        // to /account/mfa/setup is misleading — enrolling will not unlock the
        // page they were denied on. Defer those cases to the default 403.
        bool foundRequireMfa = false;
        foreach (IAuthorizationRequirement requirement in failure.FailedRequirements)
        {
            if (requirement is RequireMfaRequirement)
            {
                foundRequireMfa = true;
            }
            else
            {
                // A non-MFA requirement also failed — let the default handler decide.
                return false;
            }
        }

        if (!foundRequireMfa)
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
