using System;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.Core.Auditing;
using Heimdall.Core.Interfaces;
using Heimdall.Web.Bootstrap;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Heimdall.Web.Authorization.Policies;

/// <summary>
/// Real authorization handler for <see cref="RequireMfaRequirement"/> wired in
/// Phase 4.6 step 16 of <c>docs/implementation/phase-4-checklist.md</c>. Grants
/// the requirement when the actor is either:
/// <list type="bullet">
/// <item><description><b>Not</b> an admin of the seed organization (per OpenFGA
/// <c>organization#admin</c>) — non-privileged users do not need MFA to use the
/// app; or</description></item>
/// <item><description>An admin of the seed organization, AND the current
/// session contains the <c>amr=mfa</c> claim, AND the actor's
/// <c>users.two_factor_enabled</c> column is currently <c>true</c>.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Deny-closed on sidecar outage (Phase 3.5 step 10 parity).</b> The FGA
/// adapter's contract (<see cref="IOpenFgaAuthorizationService.CheckAsync"/>)
/// is to <i>return <c>false</c></i> on transport / 5xx failures, which is
/// indistinguishable from a genuine "user is not an org admin" answer at this
/// layer. A naive "false → non-admin → succeed" would therefore silently
/// fail-open during an outage and let a system admin missing MFA reach
/// <c>/admin/*</c> (which is gated by the DB-only <c>SystemAdmin</c> policy
/// that <i>allows</i>, not denies, on outage). To close that hole, when the
/// FGA probe returns <c>false</c> the handler always re-probes the DB-only
/// <c>users.system_admin</c> flag and treats system admins as if they were
/// org admins for the purposes of this gate. A failed DB probe denies. Only
/// a confirmed non-system-admin with a confirmed non-admin FGA answer takes
/// the "succeed unconditionally" branch (Phase 4.6 step 16 sub-bullet 4).
/// </para>
/// <para>
/// <b>Break-glass (Phase 3.5 step 10 parity).</b> When an actor would
/// otherwise be denied — either the FGA probe failed or the MFA invariants
/// failed — the handler consults the DB-only <c>system_admin</c> flag via
/// <see cref="IUserLookup"/> only if the <c>HEIMDALL_AUTHZ_BREAK_GLASS</c>
/// environment variable / configuration entry is truthy. A successful
/// override writes a <see cref="MfaBreakGlassAuditEventType"/>
/// (<c>mfa_policy_break_glass</c>) audit row before calling
/// <see cref="AuthorizationHandlerContext.Succeed(IAuthorizationRequirement)"/>;
/// a failed audit-write denies. This mirrors
/// <see cref="OpenFgaAuthorizationHandler"/>'s break-glass shape so operators
/// have a single mental model — only the audit event-type differs so the
/// downstream alerting tier can distinguish MFA-policy overrides from
/// generic FGA-policy overrides.
/// </para>
/// <para>
/// <b>Defence-in-depth.</b> The handler checks the <c>amr</c> claim
/// <i>and</i> re-reads <c>users.two_factor_enabled</c>. The DB read defeats
/// the long-lived-cookie replay attack: an admin who disables MFA on another
/// device must not retain admin access from any session whose <c>amr=mfa</c>
/// claim predates the disable.
/// </para>
/// </remarks>
public sealed class RequireMfaAuthorizationHandler : AuthorizationHandler<RequireMfaRequirement>
{
    /// <summary>
    /// The <c>amr</c> (authentication-methods-references) claim value Heimdall
    /// emits after a successful MFA challenge — mirrors the Phase 4.5 step 12
    /// login handler.
    /// </summary>
    public const string MfaAmrValue = "mfa";

    /// <summary>
    /// The standard <c>amr</c> claim type. ASP.NET Identity does not surface a
    /// constant for this, so we declare the wire name once here.
    /// </summary>
    public const string AmrClaimType = "amr";

    /// <summary>
    /// <see cref="AuditEvent.EventType"/> emitted on every successful break-glass
    /// override of the MFA gate. Distinct from
    /// <see cref="OpenFgaAuthorizationHandler.BreakGlassAuditEventType"/> so the
    /// downstream alerting tier can distinguish MFA-policy overrides from
    /// generic FGA-policy overrides — Phase 4.6 step 16 sub-bullet 5.
    /// </summary>
    public const string MfaBreakGlassAuditEventType = "mfa_policy_break_glass";

    private readonly IOpenFgaAuthorizationService _fga;
    private readonly IUserLookup _userLookup;
    private readonly IAuditEventWriter _auditWriter;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<SeedOrganizationOptions> _seedOrganizationOptions;
    private readonly ILogger<RequireMfaAuthorizationHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequireMfaAuthorizationHandler"/> class.
    /// </summary>
    /// <param name="fga">OpenFGA <c>Check</c> seam — probes <c>organization#admin</c>.</param>
    /// <param name="userLookup">Sidecar-free DB lookup for <c>two_factor_enabled</c> and break-glass <c>system_admin</c>.</param>
    /// <param name="auditWriter">Audit-event sink (for break-glass).</param>
    /// <param name="httpContextAccessor">Per-request HTTP context accessor.</param>
    /// <param name="configuration">Configuration root (consulted for break-glass enable flag).</param>
    /// <param name="seedOrganizationOptions">Bound seed-org id — the FGA admin probe is keyed on its UUID.</param>
    /// <param name="logger">Structured logger.</param>
    /// <exception cref="ArgumentNullException">If any argument is <c>null</c>.</exception>
    public RequireMfaAuthorizationHandler(
        IOpenFgaAuthorizationService fga,
        IUserLookup userLookup,
        IAuditEventWriter auditWriter,
        IHttpContextAccessor httpContextAccessor,
        IConfiguration configuration,
        IOptionsMonitor<SeedOrganizationOptions> seedOrganizationOptions,
        ILogger<RequireMfaAuthorizationHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(fga);
        ArgumentNullException.ThrowIfNull(userLookup);
        ArgumentNullException.ThrowIfNull(auditWriter);
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(seedOrganizationOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _fga = fga;
        _userLookup = userLookup;
        _auditWriter = auditWriter;
        _httpContextAccessor = httpContextAccessor;
        _configuration = configuration;
        _seedOrganizationOptions = seedOrganizationOptions;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        RequireMfaRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        // Step 1: resolve actor id. No id → deny-closed (so unauthenticated
        // requests fall through to the global auth gate, which redirects to
        // /login as it would for any other policy failure).
        string? rawActor = context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(rawActor, out Guid actorId))
        {
            return;
        }

        HttpContext? http = _httpContextAccessor.HttpContext;
        CancellationToken cancellationToken = http?.RequestAborted ?? CancellationToken.None;

        // Step 2: is the actor an admin of the seed organization? If we cannot
        // resolve the seed-org id (env var unset, bootstrap not run yet), the
        // SeedOrganizationHealthProbe should have aborted startup already —
        // defence-in-depth here, deny-closed.
        Guid seedOrgId = _seedOrganizationOptions.CurrentValue.OrganizationId;
        if (seedOrgId == Guid.Empty)
        {
            _logger.LogWarning(
                "Deny-closed: RequireMfa evaluated before SeedOrganizationOptions resolved an id.");
            return;
        }

        bool isOrgAdmin;
        try
        {
            FgaCheckRequest fgaRequest = new(
                TupleShapes.UserRef(actorId),
                TupleShapes.AdminRelation,
                TupleShapes.OrganizationRef(seedOrgId),
                // Higher consistency: an admin who just earned (or lost) the
                // admin tuple must observe that change on the very next page
                // load, even if the request-scoped cache misses.
                FgaConsistency.HigherConsistency);
            isOrgAdmin = await _fga.CheckAsync(fgaRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The adapter is already deny-closed on transport failures; catch
            // defensively here so a regression cannot crash the policy
            // pipeline. Treat as "not admin" and let break-glass decide.
            _logger.LogWarning(
                ex,
                "Deny-closed: OpenFGA Check threw for {ActorId} admin@organization:{SeedOrgId}.",
                actorId,
                seedOrgId);
            isOrgAdmin = false;
        }

        // Step 3: defence-in-depth deny-closed-on-outage.
        //
        // The FGA adapter contract returns false on transport failure, which
        // is indistinguishable here from a genuine "not an org admin" answer.
        // If we naively succeeded on every false, a system_admin missing MFA
        // could reach /admin/* during an outage (because every admin page is
        // also gated by the DB-only SystemAdmin policy, which *allows* — not
        // denies — on outage, so the chain doesn't backstop us).
        //
        // Re-probe the DB-only system_admin flag. If true, treat as admin
        // for the purposes of this gate so MFA is enforced regardless of
        // FGA state. If the DB probe itself fails, deny-closed.
        bool isSystemAdmin = false;
        if (!isOrgAdmin)
        {
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
                    "Deny-closed: system_admin probe failed for {ActorId}.",
                    actorId);
                return;
            }

            if (!isSystemAdmin)
            {
                // Confirmed non-privileged actor. Phase 4.6 step 16
                // sub-bullet 4 — succeed unconditionally.
                context.Succeed(requirement);
                return;
            }
        }

        // Step 4: privileged path (org admin per FGA, OR system admin per DB).
        // Require BOTH an amr=mfa claim on the current session AND a live
        // two_factor_enabled flag in the DB.
        bool sessionHasMfaAmr = HasMfaAmrClaim(context.User);
        bool twoFactorEnabled;
        try
        {
            twoFactorEnabled = await _userLookup
                .IsTwoFactorEnabledAsync(actorId, cancellationToken)
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
                "Deny-closed: IUserLookup.IsTwoFactorEnabledAsync threw for {ActorId}.",
                actorId);
            twoFactorEnabled = false;
        }

        if (sessionHasMfaAmr && twoFactorEnabled)
        {
            context.Succeed(requirement);
            return;
        }

        _logger.LogInformation(
            "RequireMfa denied for admin actor {ActorId}. SessionHasMfaAmr={SessionHasMfaAmr} TwoFactorEnabled={TwoFactorEnabled}.",
            actorId,
            sessionHasMfaAmr,
            twoFactorEnabled);

        // Step 5: break-glass (parity with OpenFgaAuthorizationHandler).
        if (!IsBreakGlassEnabled())
        {
            return;
        }

        // If we already determined system_admin in step 3 (FGA returned false
        // path), reuse that answer to avoid a second DB hit. Otherwise we
        // arrived here from the FGA-says-org-admin branch and need to probe.
        if (!isSystemAdmin)
        {
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
        }

        if (!isSystemAdmin)
        {
            return;
        }

        try
        {
            await WriteBreakGlassAuditAsync(actorId, seedOrgId, http, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Break-glass denied: audit write failed for {ActorId} on require_mfa.",
                actorId);
            return;
        }

        _logger.LogWarning(
            "Break-glass override granted to system_admin {ActorId} for RequireMfa policy.",
            actorId);
        context.Succeed(requirement);
    }

    private static bool HasMfaAmrClaim(ClaimsPrincipal? user)
    {
        if (user is null)
        {
            return false;
        }

        foreach (Claim claim in user.FindAll(AmrClaimType))
        {
            if (string.Equals(claim.Value, MfaAmrValue, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsBreakGlassEnabled()
    {
        string? raw = _configuration[OpenFgaAuthorizationHandler.BreakGlassConfigKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return string.Equals(raw, "1", StringComparison.Ordinal)
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private Task WriteBreakGlassAuditAsync(
        Guid actorId,
        Guid seedOrgId,
        HttpContext? http,
        CancellationToken cancellationToken)
    {
        string actorRef = TupleShapes.UserRef(actorId);
        string objectRef = TupleShapes.OrganizationRef(seedOrgId);

        string payload = JsonSerializer.Serialize(new
        {
            actor = actorRef,
            @object = objectRef,
            relation = "require_mfa",
            policy = AuthorizationPolicies.RequireMfa,
            source_ip = http?.Connection?.RemoteIpAddress?.ToString(),
            user_agent = TruncateUserAgent(http?.Request?.Headers.UserAgent.ToString()),
            timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        });

        AuditEvent auditEvent = new()
        {
            ActorUserId = actorId,
            EventType = MfaBreakGlassAuditEventType,
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
