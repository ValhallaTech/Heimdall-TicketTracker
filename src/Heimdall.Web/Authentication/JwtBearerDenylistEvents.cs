using System;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.BLL.Tokens;
using Heimdall.Core.Auditing;
using Heimdall.Core.Interfaces;
using Heimdall.Web.Bootstrap;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using StackExchange.Redis;

namespace Heimdall.Web.Authentication;

/// <summary>
/// Phase 5.5 step 12 — <see cref="JwtBearerEvents.OnTokenValidated"/> hook that
/// rejects access tokens whose <c>jti</c> appears on the Phase 5.5 Redis denylist.
/// </summary>
/// <remarks>
/// <para>
/// Outage stance, per <c>docs/implementation/phase-5-checklist.md</c> step 12: a
/// Redis transport failure is <strong>fail-closed for admin-policy-gated
/// endpoints</strong> (matching the Phase 3 step 10 deny-closed precedent on
/// OpenFGA outages) and <strong>fail-open for non-admin reads</strong>, with a
/// <see cref="AuditEventTypes.TokenAccessDenylistUnavailable"/> audit row for the
/// SOC to correlate. The admin/non-admin distinction is computed by re-issuing
/// the same <see cref="IOpenFgaAuthorizationService.CheckAsync"/> probe against
/// <c>admin@organization:&lt;seedOrgId&gt;</c> that <see cref="Heimdall.Web.Authorization.Policies.RequireMfaAuthorizationHandler"/>
/// already runs for the MFA gate — there is no second copy of the admin predicate.
/// </para>
/// <para>
/// Implementation gap vs. spec wording: fail-open scope is any request whose
/// authorization does not require organization admin (or system_admin) — not
/// strictly "reads". Extending the fail-closed posture to writes would require
/// deferring the denylist check to an authorization filter rather than
/// <see cref="JwtBearerEvents.OnTokenValidated"/> (which fires before the
/// endpoint is resolved and cannot easily probe HTTP method/endpoint metadata);
/// see Phase 5.7 follow-up.
/// </para>
/// </remarks>
public static class JwtBearerDenylistEvents
{
    /// <summary>
    /// Phase 5.5 step 12 — the actual JwtBearer <see cref="JwtBearerEvents.OnTokenValidated"/>
    /// hook. Looks up the token's <c>jti</c> on the denylist; calls
    /// <see cref="TokenValidatedContext.Fail(string)"/> on a hit; on a Redis
    /// outage, falls back to the admin-or-not check above to decide between
    /// fail-closed and fail-open.
    /// </summary>
    /// <remarks>
    /// Exception posture: Redis transient outages
    /// (<see cref="RedisConnectionException"/>, <see cref="RedisTimeoutException"/>)
    /// and shutdown-time multiplexer disposal (<see cref="ObjectDisposedException"/>,
    /// observed when the host is being torn down — mirrors the
    /// <see cref="Heimdall.DAL.Caching.RedisCacheService"/> precedent) are softened
    /// to the outage handler per the step 12 stance. All other exceptions propagate
    /// so a programming bug (NRE, DI misconfiguration, audit-writer failure, etc.)
    /// cannot silently fail-open for non-admin requests — the JwtBearer middleware
    /// will surface them as 500s, which is the correct posture.
    /// </remarks>
    /// <param name="ctx">The <see cref="TokenValidatedContext"/> from the bearer pipeline.</param>
    /// <returns>A task representing the asynchronous check.</returns>
    public static async Task OnTokenValidatedAsync(TokenValidatedContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        IServiceProvider services = ctx.HttpContext.RequestServices;
        ILoggerFactory loggerFactory = services.GetRequiredService<ILoggerFactory>();
        ILogger logger = loggerFactory.CreateLogger("JwtBearerDenylistEvents");

        string? jti = ExtractJti(ctx);
        if (string.IsNullOrWhiteSpace(jti))
        {
            // No jti claim => nothing to look up. The bearer is otherwise
            // signature-valid; downstream policy will decide whether to admit
            // it. Logged at Debug because this should never happen with tokens
            // minted by JwtTokenIssuer.
            logger.LogDebug("JwtBearer token has no jti claim; skipping denylist check.");
            return;
        }

        IAccessTokenDenylist denylist = services.GetRequiredService<IAccessTokenDenylist>();
        try
        {
            DenylistLookup lookup = await denylist
                .IsDeniedAsync(jti, ctx.HttpContext.RequestAborted)
                .ConfigureAwait(false);

            if (lookup.Denied)
            {
                logger.LogInformation(
                    "Rejecting bearer for denylisted jti {Jti}; reason={Reason}.",
                    jti,
                    lookup.Reason ?? "(unspecified)");
                ctx.Fail("denylisted");
            }

            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is RedisConnectionException || ex is RedisTimeoutException || ex is ObjectDisposedException)
        {
            // Redis transient outages and shutdown-time multiplexer disposal are
            // softened (per step 12 outage stance and the RedisCacheService
            // precedent); all other exceptions propagate.
            await HandleDenylistOutageAsync(ctx, services, logger, jti, ex).ConfigureAwait(false);
            return;
        }
    }

    private static async Task HandleDenylistOutageAsync(
        TokenValidatedContext ctx,
        IServiceProvider services,
        ILogger logger,
        string jti,
        Exception cause)
    {
        Guid actorId = ExtractActorId(ctx);

        bool isAdmin = await IsSeedOrgAdminAsync(services, logger, actorId, ctx.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (isAdmin)
        {
            logger.LogWarning(
                cause,
                "Denylist outage observed; fail-closed for admin actor {ActorId} on jti {Jti}.",
                actorId,
                jti);
            ctx.Fail("denylist_unavailable");
            return;
        }

        logger.LogWarning(
            cause,
            "Denylist outage observed; fail-open for non-admin actor {ActorId} on jti {Jti}.",
            actorId,
            jti);

        IAuditEventWriter? auditWriter = services.GetService<IAuditEventWriter>();
        if (auditWriter is null)
        {
            return;
        }

        try
        {
            await auditWriter
                .WriteAsync(
                    new AuditEvent
                    {
                        ActorUserId = actorId == Guid.Empty ? null : actorId,
                        EventType = AuditEventTypes.TokenAccessDenylistUnavailable,
                        Target = actorId == Guid.Empty ? null : actorId.ToString(),
                        Ip = ctx.HttpContext.Connection.RemoteIpAddress?.ToString(),
                        PayloadJson = JsonSerializer.Serialize(new
                        {
                            jti,
                            user_id = actorId == Guid.Empty ? null : (Guid?)actorId,
                        }),
                    },
                    ctx.HttpContext.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Failed to write denylist-unavailable audit event for jti {Jti}.",
                jti);
        }
    }

    /// <summary>
    /// Phase 5.5 step 12 — admin probe for the denylist-outage handler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This predicate must stay in lockstep with
    /// <see cref="Heimdall.Web.Authorization.Policies.RequireMfaAuthorizationHandler"/>:
    /// an actor is treated as admin if <strong>EITHER</strong> the OpenFGA
    /// <c>Check(user, admin, organization:&lt;seedOrgId&gt;)</c> probe returns
    /// true <strong>OR</strong>
    /// <see cref="IUserLookup.IsSystemAdminAsync(Guid, System.Threading.CancellationToken)"/>
    /// returns true. Otherwise a denylisted access token belonging to a
    /// <c>system_admin</c> who is not also an org admin in FGA would fail-open
    /// during a Redis outage.
    /// </para>
    /// <para>
    /// Deny-closed-on-probe-failure: if the FGA call throws (non-cancellation)
    /// we log and continue to the system_admin probe — the FGA adapter is
    /// already deny-closed on transport failure. If
    /// <see cref="IUserLookup.IsSystemAdminAsync(Guid, System.Threading.CancellationToken)"/>
    /// throws (non-cancellation), we cannot prove non-admin, so we treat the
    /// actor as admin (deny-closed) and the outage handler will fail the
    /// bearer. Cancellation always propagates.
    /// </para>
    /// </remarks>
    private static async Task<bool> IsSeedOrgAdminAsync(
        IServiceProvider services,
        ILogger logger,
        Guid actorId,
        System.Threading.CancellationToken cancellationToken)
    {
        if (actorId == Guid.Empty)
        {
            return false;
        }

        IOptionsMonitor<SeedOrganizationOptions>? optionsMonitor =
            services.GetService<IOptionsMonitor<SeedOrganizationOptions>>();
        Guid seedOrgId = optionsMonitor?.CurrentValue.OrganizationId ?? Guid.Empty;

        bool isOrgAdmin = false;
        if (seedOrgId == Guid.Empty)
        {
            // Mirror RequireMfaAuthorizationHandler — without a resolved
            // seed-org id we cannot run the FGA probe. Fall through to the
            // system_admin probe rather than returning false outright.
            logger.LogWarning(
                "Denylist outage admin probe could not resolve seed-org id; skipping FGA probe and relying on system_admin probe.");
        }
        else
        {
            IOpenFgaAuthorizationService? fga = services.GetService<IOpenFgaAuthorizationService>();
            if (fga is not null)
            {
                try
                {
                    FgaCheckRequest request = new(
                        TupleShapes.UserRef(actorId),
                        TupleShapes.AdminRelation,
                        TupleShapes.OrganizationRef(seedOrgId),
                        FgaConsistency.HigherConsistency);
                    isOrgAdmin = await fga.CheckAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "OpenFGA admin probe threw during denylist-outage handling for {ActorId}; falling through to system_admin probe.",
                        actorId);
                    isOrgAdmin = false;
                }
            }
        }

        if (isOrgAdmin)
        {
            return true;
        }

        // OR-composition with the DB-only system_admin flag — mirrors
        // RequireMfaAuthorizationHandler. RequireMfa requires IUserLookup via
        // constructor injection, so we use GetRequiredService here for parity.
        IUserLookup userLookup = services.GetRequiredService<IUserLookup>();

        try
        {
            return await userLookup.IsSystemAdminAsync(actorId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Deny-closed: we cannot prove the actor is NOT a system admin, so
            // treat them as admin. The outage handler will then fail the bearer.
            logger.LogWarning(
                ex,
                "system_admin probe threw during denylist-outage handling for {ActorId}; deny-closed (treating as admin).",
                actorId);
            return true;
        }
    }

    private static string? ExtractJti(TokenValidatedContext ctx)
    {
        if (ctx.SecurityToken is JsonWebToken jwt && !string.IsNullOrEmpty(jwt.Id))
        {
            return jwt.Id;
        }

        return ctx.Principal?.FindFirstValue("jti");
    }

    private static Guid ExtractActorId(TokenValidatedContext ctx)
    {
        string? rawSub = ctx.Principal?.FindFirstValue("sub")
            ?? ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(rawSub, out Guid actor))
        {
            return actor;
        }

        return Guid.Empty;
    }
}
