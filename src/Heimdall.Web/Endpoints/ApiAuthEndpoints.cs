using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.BLL.Tokens;
using Heimdall.Core.Auditing;
using Heimdall.Core.Models;
using Heimdall.Core.Tokens;
using Heimdall.DAL.Repositories;
using Heimdall.Web.Authorization.Policies;
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
    /// has <c>Secure=true</c>, no <c>Domain</c> attribute, and <c>Path=/</c> — we
    /// pin <c>Path</c> to the auth subtree below to scope the cookie more tightly
    /// than the bare browser rule requires.
    /// </summary>
    internal const string RefreshCookieName = "__Host-heimdall_refresh";

    /// <summary>
    /// The path the refresh cookie is scoped to. The browser will only attach the
    /// cookie to requests under this subtree, so it never leaks onto the rest of the
    /// application's surface (Razor components, the JWKS endpoint, …).
    /// </summary>
    internal const string RefreshCookiePath = "/api/v1/auth";

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
        endpoints
            .MapPost("/api/v1/auth/token", HandleIssueTokenAsync)
            .AllowAnonymous()
            .RequireRateLimiting("login")
            .RequireRateLimiting("api-token")
            .WithName("ApiAuthIssueToken");

        // Refresh is bearer-less — the request is authenticated purely by the
        // __Host-heimdall_refresh cookie, so AllowAnonymous is correct: nothing
        // about the bearer scheme should apply here.
        endpoints
            .MapPost("/api/v1/auth/refresh", HandleRefreshAsync)
            .AllowAnonymous()
            .RequireRateLimiting("api-token")
            .WithName("ApiAuthRefreshToken");

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
    /// <param name="signInManager">Identity password check (no cookie sign-in).</param>
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

        // PasswordSignInAsync (not CheckPasswordSignInAsync) — per phase-5-checklist
        // step 9, only PasswordSignInAsync populates result.RequiresTwoFactor for
        // MFA-enrolled users; CheckPasswordSignInAsync silently returns Succeeded=true
        // and bypasses MFA. lockoutOnFailure=true mirrors the browser flow's lockout
        // policy. NOTE on the TwoFactorUserId cookie: on RequiresTwoFactor,
        // PasswordSignInAsync intentionally signs the user into Identity's
        // TwoFactorUserId cookie. We deliberately leave that cookie set after
        // returning 401 {"requires_two_factor": true} — the checklist requires the
        // client to then call the existing UI MFA challenge to obtain a cookie
        // principal carrying amr=mfa, and that challenge consumes exactly this
        // TwoFactorUserId cookie. Do NOT clear it here as a "tidy-up" — doing so
        // breaks the documented two-call MFA flow.
        Microsoft.AspNetCore.Identity.SignInResult result = await signInManager
            .PasswordSignInAsync(user, password, isPersistent: false, lockoutOnFailure: true)
            .ConfigureAwait(false);

        if (result.RequiresTwoFactor)
        {
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

            // __Host- prefix forbids Domain; we set Path explicitly to scope the
            // cookie below browser-default "/".
            Path = RefreshCookiePath,
            Expires = expires,
        };
        httpContext.Response.Cookies.Append(RefreshCookieName, plaintext, options);
    }

    private static void ExpireRefreshCookie(HttpContext httpContext)
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

        foreach (Claim claim in principal.FindAll(RequireMfaAuthorizationHandler.AmrClaimType))
        {
            if (string.Equals(claim.Value, RequireMfaAuthorizationHandler.MfaAmrValue, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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
}
