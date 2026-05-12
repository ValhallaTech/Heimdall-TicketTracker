using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Auditing;
using Heimdall.Core.Email;
using Heimdall.Core.Models;
using Heimdall.Web.Authorization.Policies;
using Heimdall.Web.Email;
using Heimdall.Web.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
            .AllowAnonymous()
            .WithName("Account_Login")
            .RequireRateLimiting("login");

        endpoints.MapPost("/account/logout", HandleLogoutAsync)
            .AllowAnonymous()
            .WithName("Account_Logout");

        // --- Phase 1 step 10: email-driven self-service flows -----------------
        // Forgot/reset password and self-service registration. All four are
        // gated at the handler level on EmailFlowGate.IsActive (and registration
        // additionally on RegistrationOptions.Enabled) so that wiring SMTP turns
        // them on, and removing it turns them off — no code change required.
        endpoints.MapPost("/account/forgot-password", HandleForgotPasswordAsync)
            .AllowAnonymous()
            .WithName("Account_ForgotPassword")
            .RequireRateLimiting("password-reset");

        endpoints.MapPost("/account/reset-password", HandleResetPasswordAsync)
            .AllowAnonymous()
            .WithName("Account_ResetPassword")
            .RequireRateLimiting("password-reset");

        endpoints.MapPost("/account/register", HandleRegisterAsync)
            .AllowAnonymous()
            .WithName("Account_Register");

        endpoints.MapGet("/account/confirm-email", HandleConfirmEmailAsync)
            .AllowAnonymous()
            .WithName("Account_ConfirmEmail");

        // --- Phase 4.3 steps 10–11: MFA enrolment + disable -------------------
        // Both endpoints are cookie-gated on the IsAuthenticated policy (NOT on
        // the RequireMfa placeholder — that would lock the user out of the very
        // pages they need to enrol on). Both bind to the "mfa-setup" rate limit
        // policy keyed on (ip, user_id) — see Program.cs.
        endpoints.MapPost("/account/mfa/setup/verify", HandleMfaSetupVerifyAsync)
            .RequireAuthorization(AuthorizationPolicies.IsAuthenticated)
            .WithName("Account_MfaSetupVerify")
            .RequireRateLimiting("mfa-setup");

        endpoints.MapPost("/account/mfa/disable", HandleMfaDisableAsync)
            .RequireAuthorization(AuthorizationPolicies.IsAuthenticated)
            .WithName("Account_MfaDisable")
            .RequireRateLimiting("mfa-setup");

        // --- Phase 4.5 steps 12–14: MFA challenge / recovery / regenerate -----
        // Challenge + recovery run BEFORE the application cookie is issued (the
        // user is in the 2FA hand-off state), so they're AllowAnonymous and
        // rely on Identity's TwoFactorUserId cookie. Regenerate is post-login,
        // so it's gated on IsAuthenticated.
        endpoints.MapPost("/account/mfa/challenge", HandleMfaChallengeAsync)
            .AllowAnonymous()
            .WithName("Account_MfaChallenge")
            .RequireRateLimiting("mfa-challenge");

        endpoints.MapPost("/account/mfa/recovery", HandleMfaRecoveryAsync)
            .AllowAnonymous()
            .WithName("Account_MfaRecovery")
            .RequireRateLimiting("mfa-challenge");

        endpoints.MapPost("/account/mfa/recovery-codes/regenerate", HandleMfaRecoveryCodesRegenerateAsync)
            .RequireAuthorization(AuthorizationPolicies.IsAuthenticated)
            .WithName("Account_MfaRecoveryCodesRegenerate")
            .RequireRateLimiting("mfa-setup");

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
        [FromForm] bool? rememberMe,
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
            .PasswordSignInAsync(user, password, isPersistent: rememberMe == true, lockoutOnFailure: true)
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

        // Phase 4.5 step 12 — RequiresTwoFactor: the password was correct but
        // the user has MFA enabled. PasswordSignInAsync has already stashed the
        // two-factor user id in the Identity TwoFactorUserId cookie, so we do
        // NOT call SignInAsync here. The challenge POST consumes that cookie
        // via signInManager.TwoFactorAuthenticatorSignInAsync, which then
        // upgrades to the ApplicationScheme cookie with amr=mfa.
        if (result.RequiresTwoFactor)
        {
            string twoFactorPayload = JsonSerializer.Serialize(new { user_id = user.Id });
            await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
            {
                ActorUserId = user.Id,
                EventType = "mfa.challenge_required",
                Target = user.Id.ToString(),
                Ip = ip,
                UserAgent = userAgent,
                PayloadJson = twoFactorPayload,
            }, cancellationToken).ConfigureAwait(false);

            string safeReturnUrl = IsLocalReturnUrl(returnUrl) ? returnUrl! : "/";
            string challengeRedirect =
                $"/account/mfa/challenge?returnUrl={Uri.EscapeDataString(safeReturnUrl)}"
                + $"&rememberMe={(rememberMe == true ? "true" : "false")}";
            return Results.Redirect(challengeRedirect, permanent: false);
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
    // Forgot-password / reset-password / register / confirm-email (Phase 1 step 10)
    // -----------------------------------------------------------------------------

    /// <summary>
    /// Handles a forgot-password form post. Always redirects to
    /// <c>/forgot-password-confirmation</c> regardless of whether the supplied
    /// email matches a real account — surfacing existence here would expose a
    /// user-enumeration oracle. When <see cref="EmailFlowGate.IsActive"/> is
    /// false, redirects to <c>/forgot-password?error=disabled</c> instead.
    /// </summary>
    internal static async Task<IResult> HandleForgotPasswordAsync(
        HttpContext httpContext,
        [FromForm] string email,
        [FromServices] UserManager<HeimdallUser> userManager,
        [FromServices] IEmailSender emailSender,
        [FromServices] EmailFlowGate gate,
        [FromServices] IAuditEventWriter auditWriter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(emailSender);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(auditWriter);

        if (!gate.IsActive)
        {
            return Results.Redirect("/forgot-password?error=disabled", permanent: false);
        }

        email ??= string.Empty;

        string? ip = httpContext.Connection.RemoteIpAddress?.ToString();
        string? userAgent = TruncateUserAgent(httpContext.Request.Headers.UserAgent.ToString());
        string emailDomain = ExtractEmailDomain(email);

        var user = await userManager.FindByEmailAsync(email).ConfigureAwait(false);

        if (user is not null && user.EmailConfirmed)
        {
            // Generate a single-use token and mail the reset link. Token lifetime
            // is governed by Identity's DataProtectorTokenProvider defaults — we
            // do not extend it here.
            string token = await userManager.GeneratePasswordResetTokenAsync(user).ConfigureAwait(false);
            string resetUrl =
                $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/reset-password" +
                $"?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";

            var message = new EmailMessage
            {
                To = email,
                Subject = "Reset your Heimdall password",
                HtmlBody =
                    "<p>A password reset was requested for your Heimdall account.</p>" +
                    $"<p><a href=\"{resetUrl}\">Reset your password</a></p>" +
                    "<p>If you did not request this, you can safely ignore this email. " +
                    "The link will expire automatically.</p>",
                PlainTextBody =
                    "A password reset was requested for your Heimdall account.\n\n" +
                    $"Reset your password: {resetUrl}\n\n" +
                    "If you did not request this, you can safely ignore this email. " +
                    "The link will expire automatically.",
            };

            try
            {
                await emailSender.SendAsync(message, cancellationToken).ConfigureAwait(false);

                string payload = JsonSerializer.Serialize(new { email_domain = emailDomain });
                await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
                {
                    ActorUserId = user.Id,
                    EventType = "password.reset.requested",
                    Target = user.Id.ToString(),
                    Ip = ip,
                    UserAgent = userAgent,
                    PayloadJson = payload,
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ILoggerFactory loggerFactory = httpContext.RequestServices.GetRequiredService<ILoggerFactory>();
                ILogger logger = loggerFactory.CreateLogger("AccountEndpoints");
                logger.LogError(
                    ex,
                    "Password-reset email send failed for user {UserId}; redirecting to generic confirmation.",
                    user.Id);

                string failPayload = JsonSerializer.Serialize(new { email_domain = emailDomain });
                await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
                {
                    ActorUserId = user.Id,
                    EventType = "password.reset.send_failed",
                    Target = user.Id.ToString(),
                    Ip = ip,
                    UserAgent = userAgent,
                    PayloadJson = failPayload,
                }, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            // Timing-attack mitigation, mirroring the unknown-user login branch.
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);

            string payload = JsonSerializer.Serialize(new { submitted_email_domain = emailDomain });
            await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
            {
                ActorUserId = null,
                EventType = "password.reset.requested.unknown_email",
                Target = null,
                Ip = ip,
                UserAgent = userAgent,
                PayloadJson = payload,
            }, cancellationToken).ConfigureAwait(false);
        }

        return Results.Redirect("/forgot-password-confirmation", permanent: false);
    }

    /// <summary>
    /// Handles a reset-password form post. Validates the supplied token via
    /// <see cref="UserManager{TUser}.ResetPasswordAsync"/> (which rotates the
    /// security stamp on success). Returns 404 when the email gate is inactive
    /// — that flow cannot proceed without email anyway.
    /// </summary>
    internal static async Task<IResult> HandleResetPasswordAsync(
        HttpContext httpContext,
        [FromForm] string email,
        [FromForm] string token,
        [FromForm] string password,
        [FromForm] string confirmPassword,
        [FromServices] UserManager<HeimdallUser> userManager,
        [FromServices] EmailFlowGate gate,
        [FromServices] IAuditEventWriter auditWriter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(auditWriter);

        if (!gate.IsActive)
        {
            return Results.NotFound();
        }

        email ??= string.Empty;
        token ??= string.Empty;
        password ??= string.Empty;
        confirmPassword ??= string.Empty;

        string? ip = httpContext.Connection.RemoteIpAddress?.ToString();
        string? userAgent = TruncateUserAgent(httpContext.Request.Headers.UserAgent.ToString());
        string emailDomain = ExtractEmailDomain(email);

        if (!string.Equals(password, confirmPassword, StringComparison.Ordinal))
        {
            return Results.Redirect(
                $"/reset-password?email={Uri.EscapeDataString(email)}" +
                $"&token={Uri.EscapeDataString(token)}&error=passwords-mismatch",
                permanent: false);
        }

        var user = await userManager.FindByEmailAsync(email).ConfigureAwait(false);
        if (user is null)
        {
            string unknownPayload = JsonSerializer.Serialize(new { submitted_email_domain = emailDomain });
            await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
            {
                ActorUserId = null,
                EventType = "password.reset.failure.unknown_email",
                Target = null,
                Ip = ip,
                UserAgent = userAgent,
                PayloadJson = unknownPayload,
            }, cancellationToken).ConfigureAwait(false);

            // Never disclose whether the email or the token was the problem.
            return Results.Redirect("/reset-password?error=invalid-token", permanent: false);
        }

        var result = await userManager.ResetPasswordAsync(user, token, password).ConfigureAwait(false);

        if (result.Succeeded)
        {
            string payload = JsonSerializer.Serialize(new { email_domain = emailDomain });
            await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
            {
                ActorUserId = user.Id,
                EventType = "password.reset.success",
                Target = user.Id.ToString(),
                Ip = ip,
                UserAgent = userAgent,
                PayloadJson = payload,
            }, cancellationToken).ConfigureAwait(false);

            return Results.Redirect("/login?reset=success", permanent: false);
        }

        string failPayload = JsonSerializer.Serialize(new
        {
            email_domain = emailDomain,
            error_codes = ExtractIdentityErrorCodes(result),
        });
        await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
        {
            ActorUserId = user.Id,
            EventType = "password.reset.failure.invalid_token",
            Target = user.Id.ToString(),
            Ip = ip,
            UserAgent = userAgent,
            PayloadJson = failPayload,
        }, cancellationToken).ConfigureAwait(false);

        return Results.Redirect(
            $"/reset-password?email={Uri.EscapeDataString(email)}" +
            $"&token={Uri.EscapeDataString(token)}&error=invalid-token",
            permanent: false);
    }

    /// <summary>
    /// Handles a self-service registration form post. Gated on <em>both</em>
    /// <see cref="EmailFlowGate.IsActive"/> and <see cref="RegistrationOptions.Enabled"/>;
    /// either being off returns 404 so the page is indistinguishable from an
    /// un-deployed feature. The new account is created with
    /// <c>EmailConfirmed = false</c>; a confirmation link is mailed and the user
    /// must click it before the account can be used (Identity's
    /// <c>RequireConfirmedEmail</c> remains off in Phase 1, but the audit trail
    /// makes the confirmation visible).
    /// </summary>
    internal static async Task<IResult> HandleRegisterAsync(
        HttpContext httpContext,
        [FromForm] string email,
        [FromForm] string password,
        [FromForm] string confirmPassword,
        [FromServices] UserManager<HeimdallUser> userManager,
        [FromServices] IEmailSender emailSender,
        [FromServices] EmailFlowGate gate,
        [FromServices] IOptions<RegistrationOptions> registrationOptions,
        [FromServices] IAuditEventWriter auditWriter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(emailSender);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(registrationOptions);
        ArgumentNullException.ThrowIfNull(auditWriter);

        if (!gate.IsActive || !(registrationOptions.Value?.Enabled ?? false))
        {
            return Results.NotFound();
        }

        email ??= string.Empty;
        password ??= string.Empty;
        confirmPassword ??= string.Empty;

        string? ip = httpContext.Connection.RemoteIpAddress?.ToString();
        string? userAgent = TruncateUserAgent(httpContext.Request.Headers.UserAgent.ToString());
        string emailDomain = ExtractEmailDomain(email);

        if (!IsLikelyEmail(email))
        {
            return Results.Redirect("/register?error=InvalidEmail", permanent: false);
        }

        if (!string.Equals(password, confirmPassword, StringComparison.Ordinal))
        {
            return Results.Redirect("/register?error=PasswordMismatch", permanent: false);
        }

        var user = new HeimdallUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = userManager.NormalizeEmail(email),
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            EmailConfirmed = false,
        };

        var createResult = await userManager.CreateAsync(user, password).ConfigureAwait(false);

        if (!createResult.Succeeded)
        {
            string[] errorCodes = ExtractIdentityErrorCodes(createResult);
            string failPayload = JsonSerializer.Serialize(new
            {
                email_domain = emailDomain,
                error_codes = errorCodes,
            });
            await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
            {
                ActorUserId = null,
                EventType = "account.register.failure",
                Target = null,
                Ip = ip,
                UserAgent = userAgent,
                PayloadJson = failPayload,
            }, cancellationToken).ConfigureAwait(false);

            string firstCode = errorCodes.Length > 0 ? errorCodes[0] : "Unknown";
            return Results.Redirect($"/register?error={Uri.EscapeDataString(firstCode)}", permanent: false);
        }

        // Mail the confirmation link. Send failures must NOT roll back the
        // newly-created account — the user can re-request via forgot-password
        // or an admin can resend — but we do audit the failure.
        string confirmToken = await userManager.GenerateEmailConfirmationTokenAsync(user).ConfigureAwait(false);
        string confirmUrl =
            $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/account/confirm-email" +
            $"?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(confirmToken)}";

        var message = new EmailMessage
        {
            To = email,
            Subject = "Confirm your Heimdall account",
            HtmlBody =
                "<p>Welcome to Heimdall! Please confirm your email address to activate your account.</p>" +
                $"<p><a href=\"{confirmUrl}\">Confirm your email</a></p>",
            PlainTextBody =
                "Welcome to Heimdall! Please confirm your email address to activate your account.\n\n" +
                $"Confirm your email: {confirmUrl}",
        };

        try
        {
            await emailSender.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ILoggerFactory loggerFactory = httpContext.RequestServices.GetRequiredService<ILoggerFactory>();
            ILogger logger = loggerFactory.CreateLogger("AccountEndpoints");
            logger.LogError(
                ex,
                "Account-confirmation email send failed for new user {UserId}; account remains in unconfirmed state.",
                user.Id);

            string sendFailPayload = JsonSerializer.Serialize(new
            {
                email_domain = emailDomain,
                exception_type = ex.GetType().FullName,
            });
            await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
            {
                ActorUserId = user.Id,
                EventType = "account.register.confirmation_email.failure",
                Target = user.Id.ToString(),
                Ip = ip,
                UserAgent = userAgent,
                PayloadJson = sendFailPayload,
            }, cancellationToken).ConfigureAwait(false);
        }

        string successPayload = JsonSerializer.Serialize(new { email_domain = emailDomain });
        await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
        {
            ActorUserId = user.Id,
            EventType = "account.register.success",
            Target = user.Id.ToString(),
            Ip = ip,
            UserAgent = userAgent,
            PayloadJson = successPayload,
        }, cancellationToken).ConfigureAwait(false);

        return Results.Redirect("/register-confirmation", permanent: false);
    }

    /// <summary>
    /// Handles the GET-based email-confirmation link. The token is single-use
    /// and short-lived, so a GET is acceptable here. Redirects to <c>/login</c>
    /// with <c>?confirm=success</c> or <c>?confirm=invalid</c>; the confirmation
    /// outcome is also audited regardless.
    /// </summary>
    internal static async Task<IResult> HandleConfirmEmailAsync(
        HttpContext httpContext,
        [FromQuery] string email,
        [FromQuery] string token,
        [FromServices] UserManager<HeimdallUser> userManager,
        [FromServices] IAuditEventWriter auditWriter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(auditWriter);

        email ??= string.Empty;
        token ??= string.Empty;

        string? ip = httpContext.Connection.RemoteIpAddress?.ToString();
        string? userAgent = TruncateUserAgent(httpContext.Request.Headers.UserAgent.ToString());
        string emailDomain = ExtractEmailDomain(email);

        var user = await userManager.FindByEmailAsync(email).ConfigureAwait(false);
        if (user is null)
        {
            string unknownPayload = JsonSerializer.Serialize(new { submitted_email_domain = emailDomain });
            await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
            {
                ActorUserId = null,
                EventType = "account.confirm_email.failure.unknown_email",
                Target = null,
                Ip = ip,
                UserAgent = userAgent,
                PayloadJson = unknownPayload,
            }, cancellationToken).ConfigureAwait(false);

            return Results.Redirect("/login?confirm=invalid", permanent: false);
        }

        var result = await userManager.ConfirmEmailAsync(user, token).ConfigureAwait(false);

        if (result.Succeeded)
        {
            string payload = JsonSerializer.Serialize(new { email_domain = emailDomain });
            await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
            {
                ActorUserId = user.Id,
                EventType = "account.confirm_email.success",
                Target = user.Id.ToString(),
                Ip = ip,
                UserAgent = userAgent,
                PayloadJson = payload,
            }, cancellationToken).ConfigureAwait(false);

            return Results.Redirect("/login?confirm=success", permanent: false);
        }

        string failPayload = JsonSerializer.Serialize(new
        {
            email_domain = emailDomain,
            error_codes = ExtractIdentityErrorCodes(result),
        });
        await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
        {
            ActorUserId = user.Id,
            EventType = "account.confirm_email.failure.invalid_token",
            Target = user.Id.ToString(),
            Ip = ip,
            UserAgent = userAgent,
            PayloadJson = failPayload,
        }, cancellationToken).ConfigureAwait(false);

        return Results.Redirect("/login?confirm=invalid", permanent: false);
    }

    // -----------------------------------------------------------------------------
    // MFA enrolment / disable (Phase 4.3 steps 10–11)
    // -----------------------------------------------------------------------------

    /// <summary>
    /// Handles the MFA enrolment verify POST. On a valid TOTP code, flips
    /// <c>users.two_factor_enabled</c> (Identity rotates the security stamp),
    /// generates ten one-time recovery codes, stashes them in the short-lived
    /// <see cref="IRecoveryCodeDisplayCache"/>, and redirects to the display
    /// page. On an invalid code, writes an audit event and redirects back to
    /// the setup page with <c>?error=invalid-code</c>.
    /// </summary>
    /// <remarks>
    /// The authenticator key, the submitted code, and the generated recovery
    /// codes are never logged and never included in audit-event payloads —
    /// only counts and identifiers.
    /// </remarks>
    internal static async Task<IResult> HandleMfaSetupVerifyAsync(
        HttpContext httpContext,
        [FromForm] string code,
        [FromServices] UserManager<HeimdallUser> userManager,
        [FromServices] IRecoveryCodeDisplayCache recoveryCodeCache,
        [FromServices] IAuditEventWriter auditWriter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(recoveryCodeCache);
        ArgumentNullException.ThrowIfNull(auditWriter);

        code ??= string.Empty;

        string? ip = httpContext.Connection.RemoteIpAddress?.ToString();
        string? userAgent = TruncateUserAgent(httpContext.Request.Headers.UserAgent.ToString());

        HeimdallUser? user = await userManager.GetUserAsync(httpContext.User).ConfigureAwait(false);
        if (user is null)
        {
            return Results.Redirect("/login", permanent: false);
        }

        bool verified = await userManager
            .VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, code)
            .ConfigureAwait(false);

        if (!verified)
        {
            // Audit payload deliberately excludes the submitted code and the
            // authenticator key — both are secrets and neither belongs in
            // a queryable audit log.
            string failurePayload = JsonSerializer.Serialize(new { user_id = user.Id });
            await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
            {
                ActorUserId = user.Id,
                EventType = "mfa.enrolment.verify_failed",
                Target = user.Id.ToString(),
                Ip = ip,
                UserAgent = userAgent,
                PayloadJson = failurePayload,
            }, cancellationToken).ConfigureAwait(false);

            return Results.Redirect("/account/mfa/setup?error=invalid-code", permanent: false);
        }

        // SetTwoFactorEnabledAsync rotates the security stamp, which the
        // Phase 1 RevalidatingServerAuthenticationStateProvider observes and
        // tears down stale circuits on. Failure here means the persistence
        // layer rejected the flip (concurrency token mismatch, etc.) — do
        // NOT proceed to generate recovery codes or emit a success audit
        // event because the user is not actually enrolled.
        IdentityResult enableResult = await userManager
            .SetTwoFactorEnabledAsync(user, true)
            .ConfigureAwait(false);

        if (!enableResult.Succeeded)
        {
            string enableFailurePayload = JsonSerializer.Serialize(new { user_id = user.Id });
            await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
            {
                ActorUserId = user.Id,
                EventType = "mfa.enrolment.enable_failed",
                Target = user.Id.ToString(),
                Ip = ip,
                UserAgent = userAgent,
                PayloadJson = enableFailurePayload,
            }, cancellationToken).ConfigureAwait(false);

            return Results.Redirect("/account/mfa/setup?error=enable-failed", permanent: false);
        }

        IEnumerable<string>? generated = await userManager
            .GenerateNewTwoFactorRecoveryCodesAsync(user, 10)
            .ConfigureAwait(false);

        // GenerateNewTwoFactorRecoveryCodesAsync returns null when there is no
        // recovery-code store wired — defence in depth; this should not happen
        // in production, but if it does the user is still enrolled, just
        // without recovery codes.
        IReadOnlyList<string> codes = generated is null
            ? Array.Empty<string>()
            : new List<string>(generated);

        Guid displayToken = recoveryCodeCache.Stash(user.Id, codes);

        string successPayload = JsonSerializer.Serialize(new
        {
            user_id = user.Id,
            recovery_code_count = codes.Count,
        });
        await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
        {
            ActorUserId = user.Id,
            EventType = "mfa_enrolled",
            Target = user.Id.ToString(),
            Ip = ip,
            UserAgent = userAgent,
            PayloadJson = successPayload,
        }, cancellationToken).ConfigureAwait(false);

        string redirect =
            $"/account/mfa/recovery-codes?token={Uri.EscapeDataString(displayToken.ToString())}";
        return Results.Redirect(redirect, permanent: false);
    }

    /// <summary>
    /// Handles the MFA disable POST. Re-prompts for the current password as a
    /// stolen-cookie defence; on success, tears down the authenticator key,
    /// flips <c>users.two_factor_enabled</c> back to <c>false</c>, wipes any
    /// recovery codes, re-rotates the security stamp, signs the user out, and
    /// redirects to the login page with <c>?info=mfa-disabled</c>.
    /// </summary>
    /// <remarks>
    /// The submitted password is never logged and never included in audit
    /// payloads.
    /// </remarks>
    internal static async Task<IResult> HandleMfaDisableAsync(
        HttpContext httpContext,
        [FromForm] string password,
        [FromServices] UserManager<HeimdallUser> userManager,
        [FromServices] SignInManager<HeimdallUser> signInManager,
        [FromServices] IUserStore<HeimdallUser> userStore,
        [FromServices] IAuditEventWriter auditWriter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(signInManager);
        ArgumentNullException.ThrowIfNull(userStore);
        ArgumentNullException.ThrowIfNull(auditWriter);

        password ??= string.Empty;

        string? ip = httpContext.Connection.RemoteIpAddress?.ToString();
        string? userAgent = TruncateUserAgent(httpContext.Request.Headers.UserAgent.ToString());

        HeimdallUser? user = await userManager.GetUserAsync(httpContext.User).ConfigureAwait(false);
        if (user is null)
        {
            return Results.Redirect("/login", permanent: false);
        }

        // CheckPasswordSignInAsync without lockout — the user is already signed
        // in, so feeding into the login-attempt counter on a wrong re-entry
        // would conflate two distinct threat models.
        Microsoft.AspNetCore.Identity.SignInResult passwordResult = await signInManager
            .CheckPasswordSignInAsync(user, password, lockoutOnFailure: false)
            .ConfigureAwait(false);

        if (!passwordResult.Succeeded)
        {
            string badPayload = JsonSerializer.Serialize(new { user_id = user.Id });
            await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
            {
                ActorUserId = user.Id,
                EventType = "mfa.disable.bad_password",
                Target = user.Id.ToString(),
                Ip = ip,
                UserAgent = userAgent,
                PayloadJson = badPayload,
            }, cancellationToken).ConfigureAwait(false);

            return Results.Redirect("/account/mfa/disable?error=invalid-password", permanent: false);
        }

        // Defence in depth: validate the recovery-code store wiring BEFORE
        // mutating any user state. AddHeimdallIdentityStores registers the
        // same HeimdallUserStore against IUserStore and the recovery-code
        // store interface, so this cast must succeed in production. Throwing
        // up-front on a mis-wired host is preferable to partially disabling
        // MFA (key rotated, two_factor_enabled flipped) and then aborting
        // before recovery codes are wiped.
        if (userStore is not IUserTwoFactorRecoveryCodeStore<HeimdallUser> recoveryCodeStore)
        {
            throw new InvalidOperationException(
                "Registered IUserStore<HeimdallUser> does not implement "
                + "IUserTwoFactorRecoveryCodeStore<HeimdallUser>. Check "
                + "AddHeimdallIdentityStores wiring.");
        }

        // Tear-down sequence — order matters:
        //   1. Rotate the authenticator key to a fresh random value via
        //      ResetAuthenticatorKeyAsync, then flip TwoFactorEnabled=false.
        //      Even though the (now orphaned) row stays in user_authenticator_keys,
        //      Identity's two-factor sign-in is gated on TwoFactorEnabled and the
        //      secret has been rotated, so the previous QR/secret is meaningless.
        //      A subsequent re-enrolment via ResetAuthenticatorKeyAsync rotates
        //      again. The HeimdallUserStore.SetAuthenticatorKeyAsync contract
        //      (Phase 4.2) currently forbids null, so explicit row-deletion is
        //      out of scope for this phase.
        //   2. Wipe recovery codes via the raw store (ReplaceCodesAsync with an
        //      empty enumerable deletes all rows).
        //   3. Re-rotate the security stamp; the
        //      RevalidatingServerAuthenticationStateProvider (Phase 1 step 5)
        //      tears down any other live circuit on the next revalidation tick.
        //
        // Each IdentityResult is checked individually. A failure on any step
        // aborts the tear-down: do NOT proceed to wipe recovery codes or
        // emit a success audit event when the user's MFA state did not
        // transition cleanly.
        IdentityResult resetResult = await userManager
            .ResetAuthenticatorKeyAsync(user)
            .ConfigureAwait(false);
        if (!resetResult.Succeeded)
        {
            await WriteMfaDisableFailureAuditAsync(
                httpContext, auditWriter, user, ip, userAgent, "reset_key_failed", cancellationToken)
                .ConfigureAwait(false);
            return Results.Redirect("/account/mfa/disable?error=disable-failed", permanent: false);
        }

        IdentityResult disableResult = await userManager
            .SetTwoFactorEnabledAsync(user, false)
            .ConfigureAwait(false);
        if (!disableResult.Succeeded)
        {
            await WriteMfaDisableFailureAuditAsync(
                httpContext, auditWriter, user, ip, userAgent, "disable_flag_failed", cancellationToken)
                .ConfigureAwait(false);
            return Results.Redirect("/account/mfa/disable?error=disable-failed", permanent: false);
        }

        await recoveryCodeStore
            .ReplaceCodesAsync(user, Array.Empty<string>(), cancellationToken)
            .ConfigureAwait(false);

        await userManager.UpdateSecurityStampAsync(user).ConfigureAwait(false);

        string disabledPayload = JsonSerializer.Serialize(new { user_id = user.Id });
        await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
        {
            ActorUserId = user.Id,
            EventType = "mfa_disabled",
            Target = user.Id.ToString(),
            Ip = ip,
            UserAgent = userAgent,
            PayloadJson = disabledPayload,
        }, cancellationToken).ConfigureAwait(false);

        // Sign the cookie out so the user re-authenticates with the new
        // post-MFA security posture. The security-stamp revalidator would also
        // catch this eventually, but an explicit SignOutAsync makes the UX
        // deterministic across replicas.
        await signInManager.SignOutAsync().ConfigureAwait(false);

        return Results.Redirect("/login?info=mfa-disabled", permanent: false);
    }

    /// <summary>
    /// Phase 4.5 step 12. Consumes a TOTP code entered after a successful
    /// password login when the user has MFA enabled. On success, Identity
    /// upgrades the pending TwoFactorUserId cookie to the application cookie
    /// with the <c>amr=mfa</c> claim — that claim is what
    /// <c>RequireMfaAuthorizationHandler</c> looks for on admin-scoped pages.
    /// </summary>
    /// <remarks>
    /// AllowAnonymous because the user is in the Identity 2FA hand-off state
    /// (the application cookie has not been issued yet). The submitted code is
    /// never logged and never included in audit payloads.
    /// </remarks>
    internal static async Task<IResult> HandleMfaChallengeAsync(
        HttpContext httpContext,
        [FromForm] string code,
        [FromForm] bool? rememberMachine,
        [FromForm] bool? rememberMe,
        [FromForm] string? returnUrl,
        [FromServices] SignInManager<HeimdallUser> signInManager,
        [FromServices] UserManager<HeimdallUser> userManager,
        [FromServices] IAuditEventWriter auditWriter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(signInManager);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(auditWriter);

        code ??= string.Empty;

        string? ip = httpContext.Connection.RemoteIpAddress?.ToString();
        string? userAgent = TruncateUserAgent(httpContext.Request.Headers.UserAgent.ToString());
        string safeReturnUrl = IsLocalReturnUrl(returnUrl) ? returnUrl! : "/";
        string encodedReturn = Uri.EscapeDataString(safeReturnUrl);
        string rememberMeStr = rememberMe == true ? "true" : "false";

        HeimdallUser? user = await signInManager
            .GetTwoFactorAuthenticationUserAsync()
            .ConfigureAwait(false);
        if (user is null)
        {
            // The 2FA cookie has expired or was never minted; force the user
            // back to the login page rather than silently failing.
            return Results.Redirect("/login?error=mfa-expired", permanent: false);
        }

        // Strip whitespace and any in-code separators that authenticator apps
        // sometimes insert when the human re-types from another device.
        string authenticatorCode = code
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);

        Microsoft.AspNetCore.Identity.SignInResult result = await signInManager
            .TwoFactorAuthenticatorSignInAsync(authenticatorCode, isPersistent: rememberMe == true, rememberClient: rememberMachine == true)
            .ConfigureAwait(false);

        if (result.Succeeded)
        {
            string successPayload = JsonSerializer.Serialize(new { user_id = user.Id });
            await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
            {
                ActorUserId = user.Id,
                EventType = "mfa.challenge.succeeded",
                Target = user.Id.ToString(),
                Ip = ip,
                UserAgent = userAgent,
                PayloadJson = successPayload,
            }, cancellationToken).ConfigureAwait(false);

            return Results.Redirect(safeReturnUrl, permanent: false);
        }

        string failureReason = result.IsLockedOut
            ? "locked_out"
            : result.IsNotAllowed ? "not_allowed" : "invalid_code";

        string failurePayload = JsonSerializer.Serialize(new
        {
            user_id = user.Id,
            reason = failureReason,
        });
        await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
        {
            ActorUserId = user.Id,
            EventType = "mfa.challenge.failed",
            Target = user.Id.ToString(),
            Ip = ip,
            UserAgent = userAgent,
            PayloadJson = failurePayload,
        }, cancellationToken).ConfigureAwait(false);

        if (result.IsLockedOut)
        {
            return Results.Redirect("/login?error=locked-out", permanent: false);
        }

        return Results.Redirect(
            $"/account/mfa/challenge?returnUrl={encodedReturn}&rememberMe={rememberMeStr}&error=invalid-code",
            permanent: false);
    }

    /// <summary>
    /// Phase 4.5 step 13. Redeems a single-use recovery code in lieu of a TOTP
    /// at the MFA challenge step. On success, behaves identically to the TOTP
    /// path (Identity issues the application cookie with <c>amr=mfa</c>) and
    /// surfaces a low-codes warning on the post-redirect page when fewer than
    /// three codes remain.
    /// </summary>
    internal static async Task<IResult> HandleMfaRecoveryAsync(
        HttpContext httpContext,
        [FromForm] string code,
        [FromForm] string? returnUrl,
        [FromServices] SignInManager<HeimdallUser> signInManager,
        [FromServices] UserManager<HeimdallUser> userManager,
        [FromServices] IAuditEventWriter auditWriter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(signInManager);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(auditWriter);

        code ??= string.Empty;

        string? ip = httpContext.Connection.RemoteIpAddress?.ToString();
        string? userAgent = TruncateUserAgent(httpContext.Request.Headers.UserAgent.ToString());
        string safeReturnUrl = IsLocalReturnUrl(returnUrl) ? returnUrl! : "/";
        string encodedReturn = Uri.EscapeDataString(safeReturnUrl);

        HeimdallUser? user = await signInManager
            .GetTwoFactorAuthenticationUserAsync()
            .ConfigureAwait(false);
        if (user is null)
        {
            return Results.Redirect("/login?error=mfa-expired", permanent: false);
        }

        // Recovery codes are presented to the user with hyphen separators —
        // strip them and whitespace before handing to Identity, which expects
        // the raw code as stored.
        string recoveryCode = code
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);

        Microsoft.AspNetCore.Identity.SignInResult result = await signInManager
            .TwoFactorRecoveryCodeSignInAsync(recoveryCode)
            .ConfigureAwait(false);

        if (result.Succeeded)
        {
            int remaining = await userManager.CountRecoveryCodesAsync(user).ConfigureAwait(false);

            string successPayload = JsonSerializer.Serialize(new
            {
                user_id = user.Id,
                remaining_count = remaining,
            });
            await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
            {
                ActorUserId = user.Id,
                EventType = "mfa.recovery.redeemed",
                Target = user.Id.ToString(),
                Ip = ip,
                UserAgent = userAgent,
                PayloadJson = successPayload,
            }, cancellationToken).ConfigureAwait(false);

            // Low-codes warning surfaces via a query-string flag the post-redirect
            // page reads. We avoid baking the count into the URL to keep the
            // surface minimal — the warning is binary.
            string redirect = remaining < 3
                ? $"{safeReturnUrl}{(safeReturnUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?")}mfaRecoveryWarning=low"
                : safeReturnUrl;
            return Results.Redirect(redirect, permanent: false);
        }

        string failureReason = result.IsLockedOut ? "locked_out" : "invalid_code";
        string failurePayload = JsonSerializer.Serialize(new
        {
            user_id = user.Id,
            reason = failureReason,
        });
        await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
        {
            ActorUserId = user.Id,
            EventType = "mfa.recovery.failed",
            Target = user.Id.ToString(),
            Ip = ip,
            UserAgent = userAgent,
            PayloadJson = failurePayload,
        }, cancellationToken).ConfigureAwait(false);

        if (result.IsLockedOut)
        {
            return Results.Redirect("/login?error=locked-out", permanent: false);
        }

        return Results.Redirect(
            $"/account/mfa/recovery?returnUrl={encodedReturn}&error=invalid-code",
            permanent: false);
    }

    /// <summary>
    /// Phase 4.5 step 14. Re-prompts for the current password (stolen-cookie
    /// defence) and, on success, generates a fresh batch of ten recovery
    /// codes — invalidating the previous batch — and redirects to the existing
    /// one-time recovery-codes display page via <see cref="IRecoveryCodeDisplayCache"/>.
    /// </summary>
    /// <remarks>
    /// The submitted password is never logged. The new codes are flashed via
    /// the existing in-memory display cache rather than written into the
    /// redirect URL or session state.
    /// </remarks>
    internal static async Task<IResult> HandleMfaRecoveryCodesRegenerateAsync(
        HttpContext httpContext,
        [FromForm] string password,
        [FromServices] UserManager<HeimdallUser> userManager,
        [FromServices] SignInManager<HeimdallUser> signInManager,
        [FromServices] IRecoveryCodeDisplayCache recoveryCodeCache,
        [FromServices] IAuditEventWriter auditWriter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(signInManager);
        ArgumentNullException.ThrowIfNull(recoveryCodeCache);
        ArgumentNullException.ThrowIfNull(auditWriter);

        password ??= string.Empty;

        string? ip = httpContext.Connection.RemoteIpAddress?.ToString();
        string? userAgent = TruncateUserAgent(httpContext.Request.Headers.UserAgent.ToString());

        HeimdallUser? user = await userManager.GetUserAsync(httpContext.User).ConfigureAwait(false);
        if (user is null)
        {
            return Results.Redirect("/login", permanent: false);
        }

        // No lockout-on-failure here — see HandleMfaDisableAsync for the
        // matching rationale: the user is signed in, so a bad re-entry is not
        // the same threat model as a bad login attempt.
        Microsoft.AspNetCore.Identity.SignInResult passwordResult = await signInManager
            .CheckPasswordSignInAsync(user, password, lockoutOnFailure: false)
            .ConfigureAwait(false);

        if (!passwordResult.Succeeded)
        {
            string badPayload = JsonSerializer.Serialize(new { user_id = user.Id });
            await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
            {
                ActorUserId = user.Id,
                EventType = "mfa.recovery_codes.regenerate.bad_password",
                Target = user.Id.ToString(),
                Ip = ip,
                UserAgent = userAgent,
                PayloadJson = badPayload,
            }, cancellationToken).ConfigureAwait(false);

            return Results.Redirect(
                "/account/mfa/recovery-codes/regenerate?error=invalid-password",
                permanent: false);
        }

        IEnumerable<string>? generated = await userManager
            .GenerateNewTwoFactorRecoveryCodesAsync(user, 10)
            .ConfigureAwait(false);

        IReadOnlyList<string> codes = generated is null
            ? Array.Empty<string>()
            : new List<string>(generated);

        Guid displayToken = recoveryCodeCache.Stash(user.Id, codes);

        string successPayload = JsonSerializer.Serialize(new
        {
            user_id = user.Id,
            recovery_code_count = codes.Count,
        });
        await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
        {
            ActorUserId = user.Id,
            EventType = "mfa.recovery_codes.regenerated",
            Target = user.Id.ToString(),
            Ip = ip,
            UserAgent = userAgent,
            PayloadJson = successPayload,
        }, cancellationToken).ConfigureAwait(false);

        string redirect =
            $"/account/mfa/recovery-codes?token={Uri.EscapeDataString(displayToken.ToString())}&from=regenerate";
        return Results.Redirect(redirect, permanent: false);
    }

    // -----------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------

    /// <summary>
    /// Writes a single-line MFA-disable failure audit event with a uniform
    /// payload shape (<c>{ user_id, reason }</c>). Keeps the per-step failure
    /// branches in <see cref="HandleMfaDisableAsync"/> short.
    /// </summary>
    private static Task WriteMfaDisableFailureAuditAsync(
        HttpContext httpContext,
        IAuditEventWriter auditWriter,
        HeimdallUser user,
        string? ip,
        string? userAgent,
        string reason,
        CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(new { user_id = user.Id, reason });
        return TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
        {
            ActorUserId = user.Id,
            EventType = "mfa.disable.failed",
            Target = user.Id.ToString(),
            Ip = ip,
            UserAgent = userAgent,
            PayloadJson = payload,
        }, cancellationToken);
    }

    private static string ExtractEmailDomain(string email)
    {
        int atIdx = email.IndexOf('@', StringComparison.Ordinal);
        return atIdx >= 0 && atIdx < email.Length - 1 ? email[(atIdx + 1)..] : string.Empty;
    }

    /// <summary>
    /// Trims the User-Agent to a sensible 512-char upper bound and normalises empty
    /// strings to <c>null</c>. Mirrors the inline truncation in the login handler.
    /// </summary>
    private static string? TruncateUserAgent(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
        {
            return null;
        }

        return userAgent.Length > 512 ? userAgent[..512] : userAgent;
    }

    /// <summary>
    /// Cheap structural check for "looks like an email" — non-empty, exactly one '@',
    /// at least one character on each side. Identity will reject obviously-broken
    /// addresses too, but this rejects them before allocating a HeimdallUser.
    /// </summary>
    private static bool IsLikelyEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        int atIdx = email.IndexOf('@', StringComparison.Ordinal);
        if (atIdx <= 0 || atIdx >= email.Length - 1)
        {
            return false;
        }

        return email.IndexOf('@', atIdx + 1) < 0;
    }

    /// <summary>
    /// Projects an <see cref="IdentityResult"/>'s errors into a simple array of
    /// error codes. Codes are not secret — see Identity's IdentityErrorDescriber —
    /// so they are safe to surface in audit payloads and (for the first one only)
    /// in error redirects.
    /// </summary>
    private static string[] ExtractIdentityErrorCodes(IdentityResult result)
    {
        if (result.Errors is null)
        {
            return Array.Empty<string>();
        }

        return result.Errors
            .Where(err => !string.IsNullOrEmpty(err.Code))
            .Select(err => err.Code)
            .ToArray();
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
        catch (Exception ex) when (ex is not OperationCanceledException)
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
