using System;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Auditing;
using Heimdall.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Heimdall.Web.Endpoints;

/// <summary>
/// Server-rendered, non-Blazor account endpoints (Phase 1 step 7 of
/// <c>docs/proposals/security-and-authorization.md</c> §9.3). Login and logout are
/// implemented as HTTP-form-posted minimal-API handlers — outside the Blazor
/// SignalR circuit — so cookie issuance and rate limiting both happen on the HTTP
/// pipeline as §3.5 requires.
/// </summary>
public static class AccountEndpoints
{
    /// <summary>
    /// Maps <c>POST /account/login</c> and <c>POST /account/logout</c> onto the supplied
    /// route builder. <c>/account/login</c> is bound to the <c>"login"</c> rate-limiter
    /// policy (registered in <c>Program.cs</c>); both endpoints rely on the framework's
    /// <see cref="Microsoft.AspNetCore.Antiforgery.IAntiforgery"/> integration that
    /// <c>app.UseAntiforgery()</c> wires into form-bound minimal APIs.
    /// </summary>
    /// <param name="endpoints">The route builder to add endpoints to.</param>
    /// <returns>The same <paramref name="endpoints"/> instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is <c>null</c>.</exception>
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/account/login", HandleLoginAsync)
            .WithName("Account_Login")
            .RequireRateLimiting("login");

        endpoints.MapPost("/account/logout", HandleLogoutAsync)
            .WithName("Account_Logout");

        return endpoints;
    }

    /// <summary>
    /// Performs a username/password sign-in. Extracted as a static method so it can be
    /// unit-tested directly without spinning up a <c>WebApplicationFactory</c>.
    /// </summary>
    internal static async Task<IResult> HandleLoginAsync(
        HttpContext httpContext,
        [FromForm] string email,
        [FromForm] string password,
        [FromForm] string? returnUrl,
        [FromServices] UserManager<HeimdallUser> userManager,
        [FromServices] SignInManager<HeimdallUser> signInManager,
        [FromServices] IAuditEventWriter auditWriter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(signInManager);
        ArgumentNullException.ThrowIfNull(auditWriter);

        // Defensive — empty form fields arrive as empty strings, not null, but be safe.
        email ??= string.Empty;
        password ??= string.Empty;

        // Connection.RemoteIpAddress is correct for direct connections; behind Render's
        // reverse proxy the original client IP only surfaces once UseForwardedHeaders is
        // wired (deferred to §3.5 hardening — not in scope for step 7).
        string? ip = httpContext.Connection.RemoteIpAddress?.ToString();

        // Cap User-Agent at 512 chars — the column is plain text but pathological agents
        // (or attackers) shouldn't be able to balloon the audit row.
        string? userAgent = httpContext.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrEmpty(userAgent))
        {
            userAgent = null;
        }
        else if (userAgent.Length > 512)
        {
            userAgent = userAgent[..512];
        }

        string emailDomain = ExtractEmailDomain(email);

        var user = await userManager.FindByEmailAsync(email).ConfigureAwait(false);
        if (user is null)
        {
            // Timing-attack mitigation: a real PasswordSignInAsync runs PBKDF2 / Argon2,
            // a no-op early-return is dramatically faster. A fixed delay narrows the
            // observable wall-clock difference between "user exists" and "user does not".
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);

            // Audit only the email *domain*, never the full submitted address — full
            // addresses are PII and the domain alone is enough to correlate spray attacks.
            string payload = JsonSerializer.Serialize(new { submitted_email_domain = emailDomain });
            await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
            {
                ActorUserId = null,
                EventType = "login.failure.unknown_user",
                Target = null,
                Ip = ip,
                UserAgent = userAgent,
                PayloadJson = payload,
            }, cancellationToken).ConfigureAwait(false);

            return Results.Redirect("/login?error=invalid-credentials", permanent: false);
        }

        var result = await signInManager
            .PasswordSignInAsync(user, password, isPersistent: false, lockoutOnFailure: true)
            .ConfigureAwait(false);

        string successPayload = JsonSerializer.Serialize(new { email_domain = emailDomain });
        string failurePayload = JsonSerializer.Serialize(new { email_domain = emailDomain });

        if (result.Succeeded)
        {
            await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
            {
                ActorUserId = user.Id,
                EventType = "login.success",
                Target = user.Id.ToString(),
                Ip = ip,
                UserAgent = userAgent,
                PayloadJson = successPayload,
            }, cancellationToken).ConfigureAwait(false);

            string redirectTo = IsLocalReturnUrl(returnUrl) ? returnUrl! : "/";
            return Results.Redirect(redirectTo, permanent: false);
        }

        string failureType;
        if (result.IsLockedOut)
        {
            failureType = "login.lockout";
        }
        else if (result.IsNotAllowed)
        {
            failureType = "login.not_allowed";
        }
        else
        {
            failureType = "login.failure.bad_password";
        }

        await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
        {
            ActorUserId = user.Id,
            EventType = failureType,
            Target = user.Id.ToString(),
            Ip = ip,
            UserAgent = userAgent,
            PayloadJson = failurePayload,
        }, cancellationToken).ConfigureAwait(false);

        // Do NOT echo the submitted email back in the redirect — keeps the URL bar
        // free of credentials and avoids reflecting attacker-controlled input.
        return Results.Redirect("/login?error=invalid-credentials", permanent: false);
    }

    /// <summary>
    /// Signs the current principal out of the cookie scheme and audits the action.
    /// </summary>
    internal static async Task<IResult> HandleLogoutAsync(
        HttpContext httpContext,
        [FromServices] SignInManager<HeimdallUser> signInManager,
        [FromServices] IAuditEventWriter auditWriter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(signInManager);
        ArgumentNullException.ThrowIfNull(auditWriter);

        await signInManager.SignOutAsync().ConfigureAwait(false);

        string? ip = httpContext.Connection.RemoteIpAddress?.ToString();
        string? userAgent = httpContext.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrEmpty(userAgent))
        {
            userAgent = null;
        }
        else if (userAgent.Length > 512)
        {
            userAgent = userAgent[..512];
        }

        Guid? actorId = null;
        string eventType = "logout.anonymous";
        if (httpContext.User?.Identity?.IsAuthenticated == true)
        {
            eventType = "logout.success";
            string? nameId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (Guid.TryParse(nameId, out Guid parsed))
            {
                actorId = parsed;
            }
        }

        await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
        {
            ActorUserId = actorId,
            EventType = eventType,
            Target = actorId?.ToString(),
            Ip = ip,
            UserAgent = userAgent,
            PayloadJson = "{}",
        }, cancellationToken).ConfigureAwait(false);

        return Results.Redirect("/", permanent: false);
    }

    // -----------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------

    private static string ExtractEmailDomain(string email)
    {
        int atIdx = email.IndexOf('@', StringComparison.Ordinal);
        return atIdx >= 0 && atIdx < email.Length - 1 ? email[(atIdx + 1)..] : string.Empty;
    }

    /// <summary>
    /// True when <paramref name="returnUrl"/> is a same-origin relative URL safe to
    /// redirect to. Rejects absolute URLs (different host), protocol-relative URLs
    /// (<c>//evil</c>) and back-slash-prefixed URLs (<c>/\evil</c>) — both of which
    /// some browsers parse as host-changing.
    /// </summary>
    private static bool IsLocalReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl))
        {
            return false;
        }

        if (returnUrl.StartsWith("//", StringComparison.Ordinal)
            || returnUrl.StartsWith("/\\", StringComparison.Ordinal))
        {
            return false;
        }

        return Uri.TryCreate(returnUrl, UriKind.Relative, out _);
    }

    /// <summary>
    /// Writes an audit event, swallowing and logging any exception. A failed audit-write
    /// must NOT break the login response — operationally that turns a downstream-DB hiccup
    /// into a UX regression for every user, and security-wise the auth decision has
    /// already been made by the time we get here.
    /// </summary>
    private static async Task TryWriteAuditAsync(
        HttpContext httpContext,
        IAuditEventWriter auditWriter,
        AuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            await auditWriter.WriteAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ILoggerFactory loggerFactory = httpContext.RequestServices.GetRequiredService<ILoggerFactory>();
            ILogger logger = loggerFactory.CreateLogger("AccountEndpoints");
            logger.LogWarning(
                ex,
                "Failed to write audit event {EventType}; auth flow continued without audit.",
                auditEvent.EventType);
        }
    }
}
