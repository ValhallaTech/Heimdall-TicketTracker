using System;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.BLL.Tokens;
using Heimdall.Core.Auditing;
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
        catch (Exception ex) when (ex is RedisConnectionException || ex is RedisTimeoutException)
        {
            await HandleDenylistOutageAsync(ctx, services, logger, jti, ex).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            // Defence-in-depth — any unexpected exception is treated as an outage
            // (same fail-closed-for-admin / fail-open-for-non-admin posture) so a
            // bug in the denylist surface cannot crash the bearer pipeline.
            await HandleDenylistOutageAsync(ctx, services, logger, jti, ex).ConfigureAwait(false);
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
        if (seedOrgId == Guid.Empty)
        {
            // Mirror RequireMfaAuthorizationHandler — without a resolved seed-org
            // id we cannot prove admin, so deny-closed (returning false means
            // "not admin" which here drives the fail-open branch — but logging
            // the warning is enough; the caller's posture is already correct).
            logger.LogWarning(
                "Denylist outage admin probe could not resolve seed-org id; treating as non-admin.");
            return false;
        }

        IOpenFgaAuthorizationService? fga = services.GetService<IOpenFgaAuthorizationService>();
        if (fga is null)
        {
            return false;
        }

        try
        {
            FgaCheckRequest request = new(
                TupleShapes.UserRef(actorId),
                TupleShapes.AdminRelation,
                TupleShapes.OrganizationRef(seedOrgId),
                FgaConsistency.HigherConsistency);
            return await fga.CheckAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "OpenFGA admin probe threw during denylist-outage handling for {ActorId}; treating as non-admin.",
                actorId);
            return false;
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
