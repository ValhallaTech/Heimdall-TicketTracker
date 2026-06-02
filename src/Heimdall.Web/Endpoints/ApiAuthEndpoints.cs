using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.BLL.Tokens;
using Heimdall.Core.Auditing;
using Heimdall.Core.Email;
using Heimdall.Core.Models;
using Heimdall.Core.Tokens;
using Heimdall.DAL.Repositories;
using Heimdall.Web.Authorization.Policies;
using Heimdall.Web.Email;
using Heimdall.Web.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HeimdallTokenOptions = Heimdall.Core.Tokens.TokenOptions;

namespace Heimdall.Web.Endpoints;

/// <summary>
/// Phase 5.4 (<c>docs/implementation/phase-5-checklist.md</c> steps 9-10) — JSON-bodied
/// machine-facing token endpoints under <c>/api/v1/auth/*</c>. Distinct from
/// <see cref="AccountEndpoints"/> (which serves the browser flow with form posts,
/// antiforgery, and cookie sign-in) — this surface speaks JSON in, JSON out, never
/// touches the application cookie, and is what mobile / first-party SPA / future
/// service-to-service callers exchange credentials at.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Token endpoint (step 9).</strong> Password-grant — issues a fresh access
/// token via <see cref="ITokenIssuer"/> plus a refresh-token family root via
/// <see cref="IRefreshTokenRepository.InsertAsync"/>; the refresh plaintext is set on
/// the <c>__Host-heimdall_refresh</c> cookie and never returned in the body.
/// </para>
/// <para>
/// <strong>Refresh endpoint (step 10).</strong> Reads the cookie, hashes the
/// presented plaintext, calls <see cref="IRefreshTokenRepository.GetByHashAsync"/>,
/// detects family-replay (a previously-rotated row presented a second time) and
/// sweeps the family via <see cref="IRefreshTokenRepository.RevokeFamilyAsync"/>, and
/// — on a clean rotation — calls <see cref="IRefreshTokenRepository.RotateAsync"/>
/// which is the only place that atomically links the predecessor row to its
/// successor in a single transaction.
/// </para>
/// <para>
/// <strong>Audit + transaction gap.</strong> Both endpoints write the
/// <c>token.access.*</c> / <c>token.refresh.*</c> audit events via the
/// non-transactional <see cref="IAuditEventWriter.WriteAsync(AuditEvent, CancellationToken)"/>
/// overload. The transactional overload exists, but folding the audit-write into the
/// same transaction as either <c>InsertAsync</c> or <c>RotateAsync</c> would require
/// the repository to surface its connection/transaction to the endpoint layer — a
/// concession to layering this checklist deliberately defers. The window is small
/// (single round-trip) and on a failed audit-write the auth decision is already
/// made, mirroring the <see cref="AccountEndpoints"/> swallow-and-log posture.
/// </para>
/// </remarks>
public static class ApiAuthEndpoints
{
    /// <summary>
    /// Name of the <c>__Host-</c>-prefixed cookie that carries the refresh-token
    /// plaintext. The <c>__Host-</c> prefix enforces (browser-side) that the cookie
    /// has <c>Secure=true</c>, no <c>Domain</c> attribute, and <c>Path=/</c>. Per
    /// the cookie prefix specification, browsers will reject a <c>__Host-</c> cookie
    /// whose <c>Path</c> attribute is not exactly <c>/</c>, so <see cref="RefreshCookiePath"/>
    /// must remain <c>"/"</c>.
    /// </summary>
    internal const string RefreshCookieName = "__Host-heimdall_refresh";

    /// <summary>
    /// The <c>Path</c> attribute applied to the refresh cookie. The <c>__Host-</c>
    /// prefix specification requires this to be exactly <c>"/"</c>; browsers reject
    /// (i.e. ignore) a <c>__Host-</c> cookie whose path is anything else.
    /// </summary>
    internal const string RefreshCookiePath = "/";

    private const string ContentTypeJson = "application/json";

    /// <summary>
    /// Maps <c>POST /api/v1/auth/token</c> and <c>POST /api/v1/auth/refresh</c> onto
    /// <paramref name="endpoints"/>. Called from <c>Program.cs</c> immediately after
    /// <see cref="AccountEndpoints.MapAccountEndpoints"/> so both surfaces participate
    /// in the same routing namespace.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns><paramref name="endpoints"/> for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is <c>null</c>.</exception>
    public static IEndpointRouteBuilder MapApiAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Both rate-limit policies compose: the login limiter remains the primary
        // (ip|email) anti-spray gate for credential submission; api-token layers an
        // additional per-(ip[,sub]) ceiling so a single attacker can't blow through
        // the global budget by cycling fresh email addresses.
        //
        // .DisableAntiforgery() is applied to every JSON-bearer route here: bearer
        // authentication is itself the CSRF defense — there is no ambient cookie
        // for a forged request to ride. The global UseAntiforgery() middleware
        // otherwise short-circuits application/json POST/PUT with a 400.
        endpoints
            .MapPost("/api/v1/auth/token", HandleIssueTokenAsync)
            .AllowAnonymous()
            .RequireRateLimiting("login")
            .RequireRateLimiting("api-token")
            .DisableAntiforgery()
            .WithName("ApiAuthIssueToken");

        // Refresh is bearer-less — the request is authenticated purely by the
        // __Host-heimdall_refresh cookie, so AllowAnonymous is correct: nothing
        // about the bearer scheme should apply here.
        endpoints
            .MapPost("/api/v1/auth/refresh", HandleRefreshAsync)
            .AllowAnonymous()
            .RequireRateLimiting("api-token")
            .DisableAntiforgery()
            .WithName("ApiAuthRefreshToken");

        // Phase 5.5 step 13 — logout. Bearer-required: the principal carries
        // the jti/exp we denylist. Reads the refresh cookie if present so the
        // refresh family can be revoked atomically with the access-token
        // denylist write; succeeds (204) even when the cookie is absent so
        // single-page apps don't have to coordinate cookie state to log out.
        endpoints
            .MapPost("/api/v1/auth/logout", HandleLogoutAsync)
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
            })
            .RequireRateLimiting("api-token")
            .DisableAntiforgery()
            .WithName("ApiAuthLogout");

        // Phase 6.3/6.4 follow-up — JSON mirrors of the email-driven self-service
        // flows in AccountEndpoints (register / forgot-password / reset-password) so
        // the SvelteKit frontend can port the Register, ForgotPassword and
        // ResetPassword pages. These are JSON-in/JSON-out and never touch the
        // application cookie. Each carries the same security/audit semantics as its
        // form-posted AccountEndpoints sibling.
        //
        // .DisableAntiforgery() is mandatory: the global UseAntiforgery() middleware
        // 400s application/json POSTs before they reach the handler. .AllowAnonymous()
        // because these are pre-authentication flows. forgot/reset bind to the same
        // "password-reset" limiter the form handlers use; register matches its form
        // sibling (no rate-limit policy).
        endpoints
            .MapPost("/api/v1/auth/register", HandleApiRegisterAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithName("ApiAuthRegister");

        endpoints
            .MapPost("/api/v1/auth/forgot-password", HandleApiForgotPasswordAsync)
            .AllowAnonymous()
            .RequireRateLimiting("password-reset")
            .DisableAntiforgery()
            .WithName("ApiAuthForgotPassword");

        endpoints
            .MapPost("/api/v1/auth/reset-password", HandleApiResetPasswordAsync)
            .AllowAnonymous()
            .RequireRateLimiting("password-reset")
            .DisableAntiforgery()
            .WithName("ApiAuthResetPassword");

        return endpoints;
    }

    /// <summary>
    /// Password-grant request body for <c>POST /api/v1/auth/token</c>.
    /// </summary>
    /// <param name="Email">The submitted email.</param>
    /// <param name="Password">The submitted password (never logged).</param>
    public sealed record TokenRequest(string? Email, string? Password);

    /// <summary>
    /// Phase 5.4 step 9 handler. Validates the submitted email + password through
    /// <see cref="SignInManager{TUser}.CheckPasswordSignInAsync"/>, mints an access
    /// token through <see cref="ITokenIssuer"/>, persists a family-root refresh
    /// token, sets the <c>__Host-heimdall_refresh</c> cookie, and returns the
    /// access-token JSON envelope.
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <param name="request">The JSON-bound request body.</param>
    /// <param name="userManager">Identity user lookup.</param>
    /// <param name="signInManager">Identity password check; also used to stamp the TwoFactorUserIdScheme cookie on the MFA path.</param>
    /// <param name="tokenIssuer">Access-token minter.</param>
    /// <param name="refreshTokens">Refresh-token repository.</param>
    /// <param name="auditWriter">Audit-event writer.</param>
    /// <param name="tokenOptions">Bound <see cref="TokenOptions"/>.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with <c>{ access_token, token_type, expires_in }</c>, or 400 with <c>{ error: "invalid_grant" }</c>.</returns>
    internal static async Task<IResult> HandleIssueTokenAsync(
        HttpContext httpContext,
        [FromBody] TokenRequest request,
        [FromServices] UserManager<HeimdallUser> userManager,
        [FromServices] SignInManager<HeimdallUser> signInManager,
        [FromServices] ITokenIssuer tokenIssuer,
        [FromServices] IRefreshTokenRepository refreshTokens,
        [FromServices] IAuditEventWriter auditWriter,
        [FromServices] IOptions<HeimdallTokenOptions> tokenOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(signInManager);
        ArgumentNullException.ThrowIfNull(tokenIssuer);
        ArgumentNullException.ThrowIfNull(refreshTokens);
        ArgumentNullException.ThrowIfNull(auditWriter);
        ArgumentNullException.ThrowIfNull(tokenOptions);

        string email = request?.Email ?? string.Empty;
        string password = request?.Password ?? string.Empty;

        string? ip = httpContext.Connection.RemoteIpAddress?.ToString();
        string? userAgent = TruncateUserAgent(httpContext.Request.Headers.UserAgent.ToString());
        string emailDomain = ExtractEmailDomain(email);

        HeimdallUser? user = await userManager.FindByEmailAsync(email).ConfigureAwait(false);
        if (user is null)
        {
            // Same timing-attack mitigation as AccountEndpoints.HandleLoginAsync —
            // a fixed delay narrows the wall-clock difference between known and
            // unknown user paths.
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);

            string failurePayload = JsonSerializer.Serialize(new
            {
                submitted_email_domain = emailDomain,
            });
            await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
            {
                ActorUserId = null,
                EventType = "login.failure.unknown_user",
                Target = null,
                Ip = ip,
                UserAgent = userAgent,
                PayloadJson = failurePayload,
            }, cancellationToken).ConfigureAwait(false);

            return InvalidGrant();
        }

        // CheckPasswordSignInAsync validates the password + lockout policy without
        // issuing any application cookie. This is essential because this endpoint
        // must remain cookie-free on its success path (see class doc comment).
        // PasswordSignInAsync (which CheckPasswordSignInAsync calls internally) would
        // issue the .Heimdall.Auth application cookie on a non-MFA success, which
        // contradicts the endpoint's contract.
        Microsoft.AspNetCore.Identity.SignInResult result = await signInManager
            .CheckPasswordSignInAsync(user, password, lockoutOnFailure: true)
            .ConfigureAwait(false);

        // If the password is valid, check whether the account requires a second
        // factor. PasswordSignInAsync is used for the MFA branch only because it is
        // the sole public API that stamps the TwoFactorUserIdScheme cookie the
        // subsequent UI MFA challenge needs. Since 2FA is confirmed enabled for this
        // user it will return RequiresTwoFactor (not Succeeded) and therefore will
        // NOT issue the application cookie.
        if (result.Succeeded && await userManager.GetTwoFactorEnabledAsync(user).ConfigureAwait(false))
        {
            await signInManager
                .PasswordSignInAsync(user, password, isPersistent: false, lockoutOnFailure: false)
                .ConfigureAwait(false);
            string mfaPayload = JsonSerializer.Serialize(new { email_domain = emailDomain });
            await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
            {
                ActorUserId = user.Id,
                EventType = "mfa.challenge_required",
                Target = user.Id.ToString(),
                Ip = ip,
                UserAgent = userAgent,
                PayloadJson = mfaPayload,
            }, cancellationToken).ConfigureAwait(false);

            return RequiresTwoFactor();
        }

        if (!result.Succeeded)
        {
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

            string failurePayload = JsonSerializer.Serialize(new { email_domain = emailDomain });
            await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
            {
                ActorUserId = user.Id,
                EventType = failureType,
                Target = user.Id.ToString(),
                Ip = ip,
                UserAgent = userAgent,
                PayloadJson = failurePayload,
            }, cancellationToken).ConfigureAwait(false);

            return InvalidGrant();
        }

        // amr composition — the password-grant path always carries "pwd". If the
        // caller already has an authenticated cookie principal that carried
        // amr=mfa (Phase 4.5 step 12 — TwoFactorAuthenticator sign-in), mirror
        // that into the JWT. The cookie principal is attached by UseAuthentication
        // even for [AllowAnonymous] endpoints, so this check works at the API surface.
        var amr = new List<string> { "pwd" };
        if (HasMfaAmrClaim(httpContext.User))
        {
            amr.Add("mfa");
        }

        IssuedAccessToken access = await tokenIssuer
            .IssueAccessTokenAsync(user, amr, cancellationToken)
            .ConfigureAwait(false);

        // Refresh-token family root — ParentId is null because nothing rotated to
        // produce this row; FamilyId == Id so the family root is self-identifying.
        HeimdallTokenOptions opts = tokenOptions.Value;
        DateTimeOffset now = access.IssuedAt;
        Guid refreshId = Guid.NewGuid();
        (string refreshPlaintext, string hash) = tokenIssuer.GenerateRefreshTokenMaterial();
        DateTime refreshExpiresAt = (now + opts.RefreshTokenLifetime).UtcDateTime;

        var refreshRow = new RefreshToken(
            Id: refreshId,
            UserId: user.Id,
            TokenHash: hash,
            FamilyId: refreshId,
            ParentId: null,
            ReplacedBy: null,
            IssuedAt: now.UtcDateTime,
            ExpiresAt: refreshExpiresAt,
            RevokedAt: null,
            RevokedReason: null);

        await refreshTokens.InsertAsync(refreshRow, cancellationToken).ConfigureAwait(false);

        SetRefreshCookie(httpContext, refreshPlaintext, new DateTimeOffset(refreshExpiresAt, TimeSpan.Zero));

        await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
        {
            ActorUserId = user.Id,
            EventType = AuditEventTypes.TokenAccessIssued,
            Target = user.Id.ToString(),
            Ip = ip,
            UserAgent = userAgent,
            PayloadJson = JsonSerializer.Serialize(new
            {
                jti = access.Jti,
                user_id = user.Id,
                amr,
            }),
        }, cancellationToken).ConfigureAwait(false);

        await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
        {
            ActorUserId = user.Id,
            EventType = AuditEventTypes.TokenRefreshIssued,
            Target = user.Id.ToString(),
            Ip = ip,
            UserAgent = userAgent,
            PayloadJson = JsonSerializer.Serialize(new
            {
                jti = refreshId,
                family_id = refreshId,
                user_id = user.Id,
            }),
        }, cancellationToken).ConfigureAwait(false);

        return Results.Json(BuildTokenResponse(access, opts.AccessTokenLifetime), statusCode: StatusCodes.Status200OK);
    }

    /// <summary>
    /// Phase 5.4 step 10 handler. Reads the <c>__Host-heimdall_refresh</c> cookie,
    /// validates the presented token against <c>refresh_tokens</c>, detects
    /// family-replay, rotates on the happy path, and returns a fresh access token
    /// (refresh-token plaintext continues to live on the cookie).
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <param name="userManager">Identity user lookup (for the rotated token's claim set).</param>
    /// <param name="tokenIssuer">Access-token minter.</param>
    /// <param name="refreshTokens">Refresh-token repository.</param>
    /// <param name="auditWriter">Audit-event writer.</param>
    /// <param name="tokenOptions">Bound <see cref="TokenOptions"/>.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with <c>{ access_token, token_type, expires_in }</c>, or 401 with <c>{ error: "invalid_grant" }</c>.</returns>
    internal static async Task<IResult> HandleRefreshAsync(
        HttpContext httpContext,
        [FromServices] UserManager<HeimdallUser> userManager,
        [FromServices] ITokenIssuer tokenIssuer,
        [FromServices] IRefreshTokenRepository refreshTokens,
        [FromServices] IAuditEventWriter auditWriter,
        [FromServices] IOptions<HeimdallTokenOptions> tokenOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(tokenIssuer);
        ArgumentNullException.ThrowIfNull(refreshTokens);
        ArgumentNullException.ThrowIfNull(auditWriter);
        ArgumentNullException.ThrowIfNull(tokenOptions);

        string? ip = httpContext.Connection.RemoteIpAddress?.ToString();
        string? userAgent = TruncateUserAgent(httpContext.Request.Headers.UserAgent.ToString());

        string? presentedPlaintext = httpContext.Request.Cookies[RefreshCookieName];
        if (string.IsNullOrEmpty(presentedPlaintext))
        {
            return UnauthorizedInvalidGrant();
        }

        string presentedHash = RefreshTokenHasher.ComputeHash(presentedPlaintext);
        RefreshToken? row = await refreshTokens
            .GetByHashAsync(presentedHash, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            // Unknown hash — expire the cookie defensively (the caller is presenting
            // something we never minted, or whose row was hard-deleted by a sweeper).
            ExpireRefreshCookie(httpContext);
            return UnauthorizedInvalidGrant();
        }

        // Family-replay detection — the row exists but is already revoked or has
        // already been rotated. Either way, this is a "previously-valid token
        // presented again" event; sweep the family per proposal §5.2.
        if (row.RevokedAt is not null || row.ReplacedBy is not null)
        {
            await refreshTokens
                .RevokeFamilyAsync(row.FamilyId, RefreshTokenRevokedReason.FamilyReplay, cancellationToken)
                .ConfigureAwait(false);

            await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
            {
                ActorUserId = row.UserId,
                EventType = AuditEventTypes.TokenRefreshReplayed,
                Target = row.UserId.ToString(),
                Ip = ip,
                UserAgent = userAgent,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    jti = row.Id,
                    family_id = row.FamilyId,
                }),
            }, cancellationToken).ConfigureAwait(false);

            await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
            {
                ActorUserId = row.UserId,
                EventType = AuditEventTypes.TokenRefreshFamilyRevoked,
                Target = row.UserId.ToString(),
                Ip = ip,
                UserAgent = userAgent,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    family_id = row.FamilyId,
                    reason = RefreshTokenRevokedReason.FamilyReplay,
                }),
            }, cancellationToken).ConfigureAwait(false);

            ExpireRefreshCookie(httpContext);
            return UnauthorizedInvalidGrant();
        }

        // Expiry — distinct from replay: a naturally-expired token does NOT trigger
        // family revocation. The caller should obtain a fresh token via /token.
        if (row.ExpiresAt <= DateTime.UtcNow)
        {
            ExpireRefreshCookie(httpContext);
            return UnauthorizedInvalidGrant();
        }

        HeimdallUser? user = await userManager.FindByIdAsync(row.UserId.ToString()).ConfigureAwait(false);
        if (user is null)
        {
            // The user behind the row no longer exists. Conservatively revoke the
            // family and refuse.
            await refreshTokens
                .RevokeFamilyAsync(row.FamilyId, RefreshTokenRevokedReason.AdminRevoke, cancellationToken)
                .ConfigureAwait(false);
            ExpireRefreshCookie(httpContext);
            return UnauthorizedInvalidGrant();
        }

        HeimdallTokenOptions opts = tokenOptions.Value;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid newId = Guid.NewGuid();
        (string newPlaintext, string newHash) = tokenIssuer.GenerateRefreshTokenMaterial();
        DateTime newExpiresAt = (now + opts.RefreshTokenLifetime).UtcDateTime;

        var successor = new RefreshToken(
            Id: newId,
            UserId: row.UserId,
            TokenHash: newHash,
            FamilyId: row.FamilyId,
            ParentId: row.Id,
            ReplacedBy: null,
            IssuedAt: now.UtcDateTime,
            ExpiresAt: newExpiresAt,
            RevokedAt: null,
            RevokedReason: null);

        bool rotated = await refreshTokens
            .RotateAsync(row.Id, successor, cancellationToken)
            .ConfigureAwait(false);

        if (!rotated)
        {
            // Lost the rotation race — a concurrent request already rotated this
            // exact row. Per the repository contract this is indistinguishable
            // from a replay attempt, so we treat it the same.
            await refreshTokens
                .RevokeFamilyAsync(row.FamilyId, RefreshTokenRevokedReason.FamilyReplay, cancellationToken)
                .ConfigureAwait(false);

            await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
            {
                ActorUserId = row.UserId,
                EventType = AuditEventTypes.TokenRefreshReplayed,
                Target = row.UserId.ToString(),
                Ip = ip,
                UserAgent = userAgent,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    jti = row.Id,
                    family_id = row.FamilyId,
                }),
            }, cancellationToken).ConfigureAwait(false);

            ExpireRefreshCookie(httpContext);
            return UnauthorizedInvalidGrant();
        }

        // Conservative amr — the refresh row does not persist the original amr
        // set, so we cannot reliably mirror "mfa" forward through rotation. The
        // proposal explicitly accepts "pwd"-only here; a stricter posture would
        // need a new column on refresh_tokens, deferred to a later phase.
        var amr = new List<string> { "pwd" };
        IssuedAccessToken access = await tokenIssuer
            .IssueAccessTokenAsync(user, amr, cancellationToken)
            .ConfigureAwait(false);

        SetRefreshCookie(httpContext, newPlaintext, new DateTimeOffset(newExpiresAt, TimeSpan.Zero));

        await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
        {
            ActorUserId = row.UserId,
            EventType = AuditEventTypes.TokenRefreshRotated,
            Target = row.UserId.ToString(),
            Ip = ip,
            UserAgent = userAgent,
            PayloadJson = JsonSerializer.Serialize(new
            {
                jti = newId,
                parent_id = row.Id,
                family_id = row.FamilyId,
            }),
        }, cancellationToken).ConfigureAwait(false);

        await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
        {
            ActorUserId = row.UserId,
            EventType = AuditEventTypes.TokenAccessIssued,
            Target = row.UserId.ToString(),
            Ip = ip,
            UserAgent = userAgent,
            PayloadJson = JsonSerializer.Serialize(new
            {
                jti = access.Jti,
                user_id = row.UserId,
                amr,
            }),
        }, cancellationToken).ConfigureAwait(false);

        return Results.Json(BuildTokenResponse(access, opts.AccessTokenLifetime), statusCode: StatusCodes.Status200OK);
    }

    private static object BuildTokenResponse(IssuedAccessToken access, TimeSpan accessLifetime) => new
    {
        access_token = access.Jwt,
        token_type = "Bearer",
        expires_in = (int)accessLifetime.TotalSeconds,
    };

    /// <summary>
    /// Phase 5.5 step 13 — bearer-authenticated logout. Reads <c>jti</c> + <c>exp</c>
    /// from the validated access-token principal and adds the jti to the Phase 5.5
    /// Redis denylist; reads the <c>__Host-heimdall_refresh</c> cookie (when present),
    /// resolves the family via the deterministic hash, and bulk-revokes every still-
    /// active member of the family; expires the refresh cookie regardless; writes
    /// <see cref="AuditEventTypes.TokenAccessRevoked"/> and (when a family was revoked)
    /// <see cref="AuditEventTypes.TokenRefreshFamilyRevoked"/> audit rows.
    /// </summary>
    /// <remarks>
    /// Returns <c>204 No Content</c> on every path that reaches a validated bearer —
    /// logout is idempotent and the client has no actionable signal to distinguish
    /// "denylist already had the jti" from "fresh entry written". The cookie /
    /// family-revoke step is best-effort: a missing cookie, an unknown hash, or an
    /// already-revoked family are all treated as no-ops; only the access-token
    /// denylist write is load-bearing for the logout's primary contract.
    /// </remarks>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <param name="denylist">The Phase 5.5 Redis denylist.</param>
    /// <param name="refreshTokens">Refresh-token repository.</param>
    /// <param name="auditWriter">Audit-event writer.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns><c>204 No Content</c>.</returns>
    internal static async Task<IResult> HandleLogoutAsync(
        HttpContext httpContext,
        [FromServices] IAccessTokenDenylist denylist,
        [FromServices] IRefreshTokenRepository refreshTokens,
        [FromServices] IAuditEventWriter auditWriter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(denylist);
        ArgumentNullException.ThrowIfNull(refreshTokens);
        ArgumentNullException.ThrowIfNull(auditWriter);

        ClaimsPrincipal principal = httpContext.User;
        string? jti = principal.FindFirstValue("jti");
        string? expRaw = principal.FindFirstValue("exp");
        string? subRaw = principal.FindFirstValue("sub");

        string? ip = httpContext.Connection.RemoteIpAddress?.ToString();
        string? userAgent = TruncateUserAgent(httpContext.Request.Headers.UserAgent.ToString());

        Guid? actorUserId = null;
        if (Guid.TryParse(subRaw, out Guid parsedSub))
        {
            actorUserId = parsedSub;
        }

        // Denylist the access jti. Best-effort transparent surface — a Redis outage
        // here must not block logout (the refresh family revoke is the durable side
        // of the contract), but we DO log it so the SOC can correlate.
        if (!string.IsNullOrWhiteSpace(jti))
        {
            DateTimeOffset expiresAt = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
            if (long.TryParse(expRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long expSeconds))
            {
                expiresAt = DateTimeOffset.FromUnixTimeSeconds(expSeconds);
            }

            try
            {
                await denylist
                    .DenyAsync(jti, expiresAt, "logout", cancellationToken)
                    .ConfigureAwait(false);

                await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
                {
                    ActorUserId = actorUserId,
                    EventType = AuditEventTypes.TokenAccessRevoked,
                    Target = actorUserId?.ToString(),
                    Ip = ip,
                    UserAgent = userAgent,
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        jti,
                        reason = "logout",
                    }),
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ILoggerFactory loggerFactory = httpContext.RequestServices.GetRequiredService<ILoggerFactory>();
                ILogger logger = loggerFactory.CreateLogger("ApiAuthEndpoints");
                logger.LogWarning(
                    ex,
                    "Logout denylist write failed for jti {Jti}; continuing with refresh revoke.",
                    jti);
            }
        }

        // Family revoke — only when we can resolve a family from the cookie. A
        // missing / unknown / already-revoked cookie is not an error.
        if (httpContext.Request.Cookies.TryGetValue(RefreshCookieName, out string? cookieValue)
            && !string.IsNullOrEmpty(cookieValue))
        {
            string hash = RefreshTokenHasher.ComputeHash(cookieValue);
            RefreshToken? row = await refreshTokens
                .GetByHashAsync(hash, cancellationToken)
                .ConfigureAwait(false);

            if (row is not null)
            {
                int revoked = await refreshTokens
                    .RevokeFamilyAsync(row.FamilyId, RefreshTokenRevokedReason.Logout, cancellationToken)
                    .ConfigureAwait(false);

                if (revoked > 0)
                {
                    await TryWriteAuditAsync(httpContext, auditWriter, new AuditEvent
                    {
                        ActorUserId = actorUserId ?? row.UserId,
                        EventType = AuditEventTypes.TokenRefreshFamilyRevoked,
                        Target = row.UserId.ToString(),
                        Ip = ip,
                        UserAgent = userAgent,
                        PayloadJson = JsonSerializer.Serialize(new
                        {
                            family_id = row.FamilyId,
                            reason = RefreshTokenRevokedReason.Logout,
                            revoked_count = revoked,
                        }),
                    }, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        ExpireRefreshCookie(httpContext);
        return Results.StatusCode(StatusCodes.Status204NoContent);
    }


    /// <summary>
    /// Builds a 400 Bad Request <c>application/json</c> result with the
    /// OAuth-style <c>{ "error": "invalid_grant" }</c> body. Used by the
    /// password-grant endpoint to refuse a credential mismatch without leaking
    /// which side of the comparison failed.
    /// </summary>
    private static IResult InvalidGrant() => Results.Json(
        new { error = "invalid_grant" },
        statusCode: StatusCodes.Status400BadRequest,
        contentType: ContentTypeJson);

    /// <summary>
    /// Builds a 401 Unauthorized <c>application/json</c> result with the
    /// <c>{ "requires_two_factor": true }</c> body required by phase-5-checklist
    /// step 9 when the credentials are valid but the user is MFA-enrolled. The
    /// caller is then expected to drive the existing UI MFA challenge to upgrade
    /// their session to <c>amr=mfa</c> before re-posting to <c>/api/v1/auth/token</c>.
    /// </summary>
    private static IResult RequiresTwoFactor() => Results.Json(
        new { requires_two_factor = true },
        statusCode: StatusCodes.Status401Unauthorized,
        contentType: ContentTypeJson);

    /// <summary>
    /// Builds a 401 Unauthorized <c>application/json</c> result with the
    /// OAuth-style <c>{ "error": "invalid_grant" }</c> body. Used by the refresh
    /// endpoint for every failure path (missing cookie, unknown hash, expired,
    /// family-replay, rotation lost-race).
    /// </summary>
    private static IResult UnauthorizedInvalidGrant() => Results.Json(
        new { error = "invalid_grant" },
        statusCode: StatusCodes.Status401Unauthorized,
        contentType: ContentTypeJson);

    private static void SetRefreshCookie(HttpContext httpContext, string plaintext, DateTimeOffset expires)
    {
        var options = new CookieOptions
        {
            HttpOnly = true,

            // __Host- prefix requires Secure=true even in development; the contract
            // says "must" so we set it unconditionally. The Phase 1 cookie scheme
            // uses SameAsRequest for TestServer compatibility — this scheme does
            // not, because the only callers are HTTPS-only (mobile / SPA) and a
            // misconfigured plain-HTTP test environment SHOULD see the cookie
            // dropped rather than silently fall through.
            Secure = true,

            // Strict (not Lax) because no cross-site flow should ever attach a
            // refresh token — the cookie only travels on first-party POSTs to
            // /api/v1/auth/refresh from a same-origin SPA.
            SameSite = SameSiteMode.Strict,

            // __Host- prefix forbids Domain; Path must be "/" per the __Host-
            // prefix specification (browsers reject the cookie otherwise).
            Path = RefreshCookiePath,
            Expires = expires,
        };
        httpContext.Response.Cookies.Append(RefreshCookieName, plaintext, options);
    }

    internal static void ExpireRefreshCookie(HttpContext httpContext)
    {
        // CookieOptions on Delete must mirror the Path/Secure used when setting,
        // or the browser will silently keep the cookie installed.
        httpContext.Response.Cookies.Delete(RefreshCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = RefreshCookiePath,
        });
    }

    /// <summary>
    /// True when the cookie principal already attached to the request carries
    /// at least one <c>amr=mfa</c> claim. Mirrors the constant used by
    /// <see cref="RequireMfaAuthorizationHandler"/> so the API-side composition
    /// and the policy-side enforcement read the same claim.
    /// </summary>
    private static bool HasMfaAmrClaim(ClaimsPrincipal principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        return principal.FindAll(RequireMfaAuthorizationHandler.AmrClaimType)
            .Any(c => string.Equals(c.Value, RequireMfaAuthorizationHandler.MfaAmrValue, StringComparison.Ordinal));
    }

    // ---- Phase 6.3/6.4 follow-up: JSON self-service auth handlers -------------

    /// <summary>
    /// JSON request body for <c>POST /api/v1/auth/register</c>.
    /// </summary>
    /// <param name="Email">The submitted email address.</param>
    /// <param name="Password">The submitted password (never logged).</param>
    /// <param name="ConfirmPassword">The password confirmation (never logged).</param>
    public sealed record RegisterRequest(string? Email, string? Password, string? ConfirmPassword);

    /// <summary>
    /// JSON request body for <c>POST /api/v1/auth/forgot-password</c>.
    /// </summary>
    /// <param name="Email">The submitted email address.</param>
    public sealed record ForgotPasswordRequest(string? Email);

    /// <summary>
    /// JSON request body for <c>POST /api/v1/auth/reset-password</c>.
    /// </summary>
    /// <param name="Email">The submitted email address.</param>
    /// <param name="Token">The single-use password-reset token from the emailed link.</param>
    /// <param name="Password">The submitted new password (never logged).</param>
    /// <param name="ConfirmPassword">The new-password confirmation (never logged).</param>
    public sealed record ResetPasswordRequest(string? Email, string? Token, string? Password, string? ConfirmPassword);

    /// <summary>
    /// JSON mirror of <see cref="AccountEndpoints.HandleRegisterAsync"/>. Gated on
    /// <em>both</em> <see cref="EmailFlowGate.IsActive"/> and
    /// <see cref="RegistrationOptions.Enabled"/>; either being off returns
    /// <c>404</c> so the surface is indistinguishable from an un-deployed feature.
    /// The new account is created with <c>EmailConfirmed = false</c>; a confirmation
    /// link is mailed, and a send failure does <em>not</em> roll back the account.
    /// The success response is intentionally generic
    /// (<c>{ "status": "confirmation_pending" }</c>) so it does not reveal whether
    /// the email was actually deliverable.
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <param name="request">The JSON-bound request body.</param>
    /// <param name="userManager">Identity user creation + normalization.</param>
    /// <param name="emailSender">Confirmation-email transport.</param>
    /// <param name="gate">Email-flow feature gate.</param>
    /// <param name="registrationOptions">Bound <see cref="RegistrationOptions"/>.</param>
    /// <param name="auditWriter">Audit-event writer.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// <c>200 { "status": "confirmation_pending" }</c> on success; <c>400</c> with
    /// <c>{ "error": "invalid_email" | "password_mismatch" | "registration_failed", ... }</c>
    /// on validation/identity failure; <c>404</c> when the gate is inactive or
    /// registration is disabled.
    /// </returns>
    internal static async Task<IResult> HandleApiRegisterAsync(
        HttpContext httpContext,
        [FromBody] RegisterRequest request,
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

        string email = request?.Email ?? string.Empty;
        string password = request?.Password ?? string.Empty;
        string confirmPassword = request?.ConfirmPassword ?? string.Empty;

        string? ip = httpContext.Connection.RemoteIpAddress?.ToString();
        string? userAgent = TruncateUserAgent(httpContext.Request.Headers.UserAgent.ToString());
        string emailDomain = ExtractEmailDomain(email);

        if (!IsLikelyEmail(email))
        {
            return Results.Json(
                new { error = "invalid_email" },
                statusCode: StatusCodes.Status400BadRequest,
                contentType: ContentTypeJson);
        }

        if (!string.Equals(password, confirmPassword, StringComparison.Ordinal))
        {
            return Results.Json(
                new { error = "password_mismatch" },
                statusCode: StatusCodes.Status400BadRequest,
                contentType: ContentTypeJson);
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

        IdentityResult createResult = await userManager.CreateAsync(user, password).ConfigureAwait(false);

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

            return Results.Json(
                new { error = "registration_failed", codes = errorCodes },
                statusCode: StatusCodes.Status400BadRequest,
                contentType: ContentTypeJson);
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
            ILogger logger = loggerFactory.CreateLogger("ApiAuthEndpoints");
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

        return Results.Json(
            new { status = "confirmation_pending" },
            statusCode: StatusCodes.Status200OK,
            contentType: ContentTypeJson);
    }

    /// <summary>
    /// JSON mirror of <see cref="AccountEndpoints.HandleForgotPasswordAsync"/>.
    /// Always returns the same generic <c>200 { "status": "ok" }</c> regardless of
    /// whether the email matches a real, confirmed account — surfacing existence
    /// here would expose a user-enumeration oracle. When
    /// <see cref="EmailFlowGate.IsActive"/> is false, returns <c>404</c>. The
    /// matched/confirmed branch mails a reset link and audits
    /// <c>password.reset.requested</c>; the unmatched branch runs the fixed
    /// <c>Task.Delay(50)</c> timing mitigation and audits
    /// <c>password.reset.requested.unknown_email</c>.
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <param name="request">The JSON-bound request body.</param>
    /// <param name="userManager">Identity user lookup + token generation.</param>
    /// <param name="emailSender">Reset-email transport.</param>
    /// <param name="gate">Email-flow feature gate.</param>
    /// <param name="auditWriter">Audit-event writer.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns><c>200 { "status": "ok" }</c> in all non-gated cases; <c>404</c> when the gate is inactive.</returns>
    internal static async Task<IResult> HandleApiForgotPasswordAsync(
        HttpContext httpContext,
        [FromBody] ForgotPasswordRequest request,
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
            return Results.NotFound();
        }

        string email = request?.Email ?? string.Empty;

        string? ip = httpContext.Connection.RemoteIpAddress?.ToString();
        string? userAgent = TruncateUserAgent(httpContext.Request.Headers.UserAgent.ToString());
        string emailDomain = ExtractEmailDomain(email);

        HeimdallUser? user = await userManager.FindByEmailAsync(email).ConfigureAwait(false);

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
                ILogger logger = loggerFactory.CreateLogger("ApiAuthEndpoints");
                logger.LogError(
                    ex,
                    "Password-reset email send failed for user {UserId}; returning generic confirmation.",
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

        return Results.Json(
            new { status = "ok" },
            statusCode: StatusCodes.Status200OK,
            contentType: ContentTypeJson);
    }

    /// <summary>
    /// JSON mirror of <see cref="AccountEndpoints.HandleResetPasswordAsync"/>.
    /// Validates the supplied token via
    /// <see cref="UserManager{TUser}.ResetPasswordAsync"/> (which rotates the
    /// security stamp on success). Returns <c>404</c> when the email gate is
    /// inactive. Never discloses whether the email or the token was wrong: an
    /// unknown user and an invalid token both produce the identical
    /// <c>400 { "error": "invalid_token" }</c> response, while the internal audit
    /// events (<c>password.reset.failure.unknown_email</c> vs
    /// <c>password.reset.failure.invalid_token</c>) stay distinct.
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <param name="request">The JSON-bound request body.</param>
    /// <param name="userManager">Identity user lookup + password reset.</param>
    /// <param name="gate">Email-flow feature gate.</param>
    /// <param name="auditWriter">Audit-event writer.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// <c>200 { "status": "ok" }</c> on success; <c>400</c> with
    /// <c>{ "error": "password_mismatch" | "invalid_token" }</c> on failure;
    /// <c>404</c> when the gate is inactive.
    /// </returns>
    internal static async Task<IResult> HandleApiResetPasswordAsync(
        HttpContext httpContext,
        [FromBody] ResetPasswordRequest request,
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

        string email = request?.Email ?? string.Empty;
        string token = request?.Token ?? string.Empty;
        string password = request?.Password ?? string.Empty;
        string confirmPassword = request?.ConfirmPassword ?? string.Empty;

        string? ip = httpContext.Connection.RemoteIpAddress?.ToString();
        string? userAgent = TruncateUserAgent(httpContext.Request.Headers.UserAgent.ToString());
        string emailDomain = ExtractEmailDomain(email);

        if (!string.Equals(password, confirmPassword, StringComparison.Ordinal))
        {
            return Results.Json(
                new { error = "password_mismatch" },
                statusCode: StatusCodes.Status400BadRequest,
                contentType: ContentTypeJson);
        }

        HeimdallUser? user = await userManager.FindByEmailAsync(email).ConfigureAwait(false);
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
            return Results.Json(
                new { error = "invalid_token" },
                statusCode: StatusCodes.Status400BadRequest,
                contentType: ContentTypeJson);
        }

        IdentityResult result = await userManager.ResetPasswordAsync(user, token, password).ConfigureAwait(false);

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

            return Results.Json(
                new { status = "ok" },
                statusCode: StatusCodes.Status200OK,
                contentType: ContentTypeJson);
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

        return Results.Json(
            new { error = "invalid_token" },
            statusCode: StatusCodes.Status400BadRequest,
            contentType: ContentTypeJson);
    }

    // ---- Local mirrors of the AccountEndpoints helpers ------------------------
    // The originals are private static so cannot be reused across files. Keeping
    // them small and identical keeps the audit-payload conventions consistent
    // (email_domain, 512-char UA cap, swallow-and-log audit failures) without
    // promoting them to a shared utility before Phase 5 has settled.

    private static string ExtractEmailDomain(string email)
    {
        int atIdx = email.IndexOf('@', StringComparison.Ordinal);
        return atIdx >= 0 && atIdx < email.Length - 1 ? email[(atIdx + 1)..] : string.Empty;
    }

    private static string? TruncateUserAgent(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
        {
            return null;
        }

        return userAgent.Length > 512 ? userAgent[..512] : userAgent;
    }

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
            ILogger logger = loggerFactory.CreateLogger("ApiAuthEndpoints");
            logger.LogWarning(
                ex,
                "Failed to write audit event {EventType}; token flow continued without audit.",
                auditEvent.EventType);
        }
    }

    /// <summary>
    /// Cheap structural check for "looks like an email" — non-empty, exactly one '@',
    /// at least one character on each side. Identity will reject obviously-broken
    /// addresses too, but this rejects them before allocating a HeimdallUser. Mirrors
    /// <c>AccountEndpoints.IsLikelyEmail</c>.
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
    /// so they are safe to surface in audit payloads and response bodies. Mirrors
    /// <c>AccountEndpoints.ExtractIdentityErrorCodes</c>.
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
}
