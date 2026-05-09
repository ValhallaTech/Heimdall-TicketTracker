using System;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.Core.Auditing;
using Heimdall.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Heimdall.Web.Authorization.Policies;

/// <summary>
/// Authorization handler for <see cref="OpenFgaRequirement"/>. Resolves the
/// actor's user id from <see cref="ClaimTypes.NameIdentifier"/> and the object
/// id from the request's route values (or query string fallback), builds an
/// <see cref="FgaCheckRequest"/>, and forwards to
/// <see cref="IOpenFgaAuthorizationService.CheckAsync"/>. Implements
/// <c>docs/proposals/openfga.md</c> §3 step 9 (policy-based <c>[Authorize]</c>)
/// and §3 step 10 (deny-closed + DB-only break-glass).
/// </summary>
/// <remarks>
/// <para>
/// <b>Deny-closed.</b> Every failure path — missing claim, unparseable actor /
/// object id, sidecar exception — leaves the requirement <i>unsatisfied</i>
/// (no <c>context.Succeed</c> call). The <see cref="IOpenFgaAuthorizationService"/>
/// adapter is itself deny-closed on transport / 5xx errors; this handler does
/// not need to add a second layer.
/// </para>
/// <para>
/// <b>Break-glass (§3 step 10).</b> When OpenFGA returns <c>false</c> (or
/// throws), this handler consults the DB-only <c>system_admin</c> flag via
/// <see cref="IUserLookup"/> only if the <c>HEIMDALL_AUTHZ_BREAK_GLASS</c>
/// environment variable / configuration entry is truthy. A successful
/// break-glass override writes an <c>authz_break_glass</c> audit row before
/// calling <see cref="AuthorizationHandlerContext.Succeed(IAuthorizationRequirement)"/>;
/// a failed audit-write denies (per the proposal — break-glass without an
/// audit trail is worse than no break-glass).
/// </para>
/// <para>
/// <b>Consistency.</b> Defaults to <see cref="FgaConsistency.MinimizeLatency"/>
/// because the request-scoped cache in the adapter already absorbs repeated
/// checks within a single page render. Per-policy overrides are not exposed
/// here — bumping individual policies to <see cref="FgaConsistency.HigherConsistency"/>
/// would couple page UX to sidecar p95, and we would prefer to revisit globally
/// after the Phase 3.7 step 13 perf measurement.
/// </para>
/// </remarks>
public sealed class OpenFgaAuthorizationHandler : AuthorizationHandler<OpenFgaRequirement>
{
    /// <summary>
    /// Configuration / environment-variable name that enables the break-glass path.
    /// </summary>
    public const string BreakGlassConfigKey = "HEIMDALL_AUTHZ_BREAK_GLASS";

    /// <summary>
    /// <see cref="AuditEvent.EventType"/> emitted on every successful break-glass override.
    /// </summary>
    public const string BreakGlassAuditEventType = "authz_break_glass";

    private readonly IOpenFgaAuthorizationService _fga;
    private readonly IUserLookup _userLookup;
    private readonly IAuditEventWriter _auditWriter;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OpenFgaAuthorizationHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenFgaAuthorizationHandler"/> class.
    /// </summary>
    /// <param name="fga">OpenFGA <c>Check</c> seam.</param>
    /// <param name="userLookup">Sidecar-free <c>system_admin</c> lookup (for break-glass).</param>
    /// <param name="auditWriter">Audit-event sink (for break-glass).</param>
    /// <param name="httpContextAccessor">Per-request HTTP context accessor.</param>
    /// <param name="configuration">Configuration root (consulted for break-glass enable flag).</param>
    /// <param name="logger">Structured logger.</param>
    /// <exception cref="ArgumentNullException">If any argument is <c>null</c>.</exception>
    public OpenFgaAuthorizationHandler(
        IOpenFgaAuthorizationService fga,
        IUserLookup userLookup,
        IAuditEventWriter auditWriter,
        IHttpContextAccessor httpContextAccessor,
        IConfiguration configuration,
        ILogger<OpenFgaAuthorizationHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(fga);
        ArgumentNullException.ThrowIfNull(userLookup);
        ArgumentNullException.ThrowIfNull(auditWriter);
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        _fga = fga;
        _userLookup = userLookup;
        _auditWriter = auditWriter;
        _httpContextAccessor = httpContextAccessor;
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OpenFgaRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        // Step 1: resolve actor id from the cookie/JWT claim. No id → deny-closed.
        string? rawActor = context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(rawActor, out Guid actorId))
        {
            return;
        }

        // Step 2: resolve object id. Pulled from route values first (the canonical
        // source per AspNetCore policy pipelines), with a query-string fallback so
        // ad-hoc internal links like /tickets?ticketId=42 still resolve.
        HttpContext? http = _httpContextAccessor.HttpContext;
        string? objectId = ResolveObjectId(http, requirement.RouteValueKey);
        if (string.IsNullOrWhiteSpace(objectId))
        {
            // No object → no decision. Deny-closed.
            return;
        }

        string objectRef = string.Create(
            CultureInfo.InvariantCulture,
            $"{requirement.ObjectType}:{objectId}");
        string actorRef = TupleShapes.UserRef(actorId);

        // Step 3: ask the sidecar.
        CancellationToken cancellationToken = http?.RequestAborted ?? CancellationToken.None;
        bool fgaAllows;
        try
        {
            FgaCheckRequest fgaRequest = new(
                actorRef,
                requirement.Relation,
                objectRef,
                FgaConsistency.MinimizeLatency);
            fgaAllows = await _fga.CheckAsync(fgaRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The adapter is already deny-closed on transport failures — but
            // catch defensively so a future regression cannot crash the policy
            // pipeline. Treat as "deny" and let the break-glass path below
            // decide whether to override.
            _logger.LogWarning(
                ex,
                "Deny-closed: OpenFGA Check threw for {ActorRef} {Relation} {ObjectRef}.",
                actorRef,
                requirement.Relation,
                objectRef);
            fgaAllows = false;
        }

        if (fgaAllows)
        {
            context.Succeed(requirement);
            return;
        }

        // Step 4: break-glass. Only if (a) the env-var enable flag is set AND
        // (b) the actor is a system_admin in the DB. Both conditions read
        // sources that do not depend on the sidecar.
        if (!IsBreakGlassEnabled())
        {
            return;
        }

        bool isSystemAdmin;
        try
        {
            isSystemAdmin = await _userLookup
                .IsSystemAdminAsync(actorId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Deny-closed: break-glass system_admin lookup failed for actor {ActorId}.",
                actorId);
            return;
        }

        if (!isSystemAdmin)
        {
            return;
        }

        // Step 5: write the audit row before granting. A failed write denies —
        // break-glass without an audit trail is the failure mode the proposal
        // most wants to avoid.
        try
        {
            await WriteBreakGlassAuditAsync(
                actorId,
                actorRef,
                objectRef,
                requirement,
                http,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Break-glass denied: audit write failed for actor {ActorRef} on {ObjectRef} relation {Relation}.",
                actorRef,
                objectRef,
                requirement.Relation);
            return;
        }

        _logger.LogWarning(
            "Break-glass override granted to system_admin {ActorRef} on {ObjectRef} relation {Relation}.",
            actorRef,
            objectRef,
            requirement.Relation);
        context.Succeed(requirement);
    }

    private static string? ResolveObjectId(HttpContext? http, string? routeKey)
    {
        if (http is null || string.IsNullOrWhiteSpace(routeKey))
        {
            return null;
        }

        if (http.Request.RouteValues.TryGetValue(routeKey, out object? routeValue)
            && routeValue is not null)
        {
            string? asString = Convert.ToString(routeValue, CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(asString))
            {
                return asString;
            }
        }

        // Query-string fallback for non-routed links (e.g. /tickets?ticketId=42).
        if (http.Request.Query.TryGetValue(routeKey, out var queryValue))
        {
            string? asString = queryValue.ToString();
            if (!string.IsNullOrWhiteSpace(asString))
            {
                return asString;
            }
        }

        return null;
    }

    private bool IsBreakGlassEnabled()
    {
        // IConfiguration on the host already merges environment variables, so
        // the same key hits HEIMDALL_AUTHZ_BREAK_GLASS regardless of source.
        string? raw = _configuration[BreakGlassConfigKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        // Truthy values per the proposal: "1" or "true" (case-insensitive).
        return string.Equals(raw, "1", StringComparison.Ordinal)
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private Task WriteBreakGlassAuditAsync(
        Guid actorId,
        string actorRef,
        string objectRef,
        OpenFgaRequirement requirement,
        HttpContext? http,
        CancellationToken cancellationToken)
    {
        // Structured payload — JSON so downstream tooling can index without
        // teaching a parser the per-event schema. Keep keys snake_case to
        // match the rest of the audit_events.payload corpus.
        string payload = JsonSerializer.Serialize(new
        {
            actor = actorRef,
            @object = objectRef,
            relation = requirement.Relation,
            object_type = requirement.ObjectType,
            route_key = requirement.RouteValueKey,
            source_ip = http?.Connection?.RemoteIpAddress?.ToString(),
            user_agent = TruncateUserAgent(http?.Request?.Headers.UserAgent.ToString()),
            timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        });

        AuditEvent auditEvent = new()
        {
            ActorUserId = actorId,
            EventType = BreakGlassAuditEventType,
            Target = objectRef,
            Ip = http?.Connection?.RemoteIpAddress?.ToString(),
            UserAgent = TruncateUserAgent(http?.Request?.Headers.UserAgent.ToString()),
            PayloadJson = payload,
        };

        return _auditWriter.WriteAsync(auditEvent, cancellationToken);
    }

    private static string? TruncateUserAgent(string? userAgent)
    {
        const int MaxUserAgentLength = 512;
        if (string.IsNullOrEmpty(userAgent))
        {
            return null;
        }

        return userAgent.Length <= MaxUserAgentLength
            ? userAgent
            : userAgent[..MaxUserAgentLength];
    }
}
