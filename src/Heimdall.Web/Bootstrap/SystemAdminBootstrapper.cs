using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Auditing;
using Heimdall.Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Heimdall.Web.Bootstrap;

/// <summary>
/// Idempotent, env-var-driven SystemAdmin bootstrap. On a first deployment to a fresh
/// database Heimdall needs at least one administrator who can sign in and start managing
/// users; we never ship credentials in source so this service reads them from the
/// <c>HEIMDALL_BOOTSTRAP_ADMIN_EMAIL</c> / <c>HEIMDALL_BOOTSTRAP_ADMIN_PASSWORD</c>
/// environment variables at startup. See Phase 1 step 8 of
/// <c>docs/proposals/security-and-authorization.md</c> §9.3.
/// </summary>
/// <remarks>
/// Idempotency contract:
/// <list type="bullet">
///   <item>No row with that email → create one with <c>system_admin = true</c>.</item>
///   <item>Row exists, <c>system_admin = false</c> → flip the flag (promote).</item>
///   <item>Row exists, <c>system_admin = true</c> → no-op (no DB write, no audit).</item>
/// </list>
/// Bootstrap failures must never abort startup — a transient DB hiccup at boot must
/// not take the whole app down. <see cref="OperationCanceledException"/> still
/// propagates so a host-level shutdown is honoured promptly.
/// </remarks>
public sealed class SystemAdminBootstrapper
{
    private readonly UserManager<HeimdallUser> _userManager;
    private readonly IUserStore<HeimdallUser> _userStore;
    private readonly IAuditEventWriter _auditWriter;
    private readonly ILogger<SystemAdminBootstrapper> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemAdminBootstrapper"/> class.
    /// </summary>
    /// <param name="userManager">Identity user manager used to look up and create the admin user.</param>
    /// <param name="userStore">
    /// Identity user store. We call <see cref="IUserStore{TUser}.UpdateAsync"/> on the
    /// interface (rather than casting to the concrete <c>HeimdallUserStore</c>) so the
    /// bootstrapper stays unit-testable with a plain Moq mock.
    /// </param>
    /// <param name="auditWriter">Audit-event sink — the only audit channel for this service.</param>
    /// <param name="logger">Structured logger.</param>
    /// <exception cref="ArgumentNullException">If any argument is <c>null</c>.</exception>
    public SystemAdminBootstrapper(
        UserManager<HeimdallUser> userManager,
        IUserStore<HeimdallUser> userStore,
        IAuditEventWriter auditWriter,
        ILogger<SystemAdminBootstrapper> logger)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(userStore);
        ArgumentNullException.ThrowIfNull(auditWriter);
        ArgumentNullException.ThrowIfNull(logger);

        _userManager = userManager;
        _userStore = userStore;
        _auditWriter = auditWriter;
        _logger = logger;
    }

    /// <summary>
    /// Runs the SystemAdmin bootstrap. If <paramref name="email"/> or <paramref name="password"/>
    /// is null/whitespace the call is a logged no-op (operators that don't want bootstrap
    /// simply leave the env vars unset). Any unexpected failure is logged at <c>Error</c>
    /// and swallowed so startup can continue; <see cref="OperationCanceledException"/> is
    /// re-thrown so cooperative cancellation is honoured.
    /// </summary>
    /// <param name="email">Bootstrap admin email (typically <c>HEIMDALL_BOOTSTRAP_ADMIN_EMAIL</c>).</param>
    /// <param name="password">
    /// Bootstrap admin password (typically <c>HEIMDALL_BOOTSTRAP_ADMIN_PASSWORD</c>). Must
    /// satisfy the configured Identity password policy or <see cref="UserManager{TUser}.CreateAsync(TUser, string)"/>
    /// will fail; the failing <c>IdentityError</c> codes/descriptions are logged so an
    /// operator can diagnose policy mismatches without scraping debug-level logs.
    /// </param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    public async Task RunAsync(string? email, string? password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            _logger.LogInformation(
                "SystemAdmin bootstrap skipped: HEIMDALL_BOOTSTRAP_ADMIN_EMAIL and/or HEIMDALL_BOOTSTRAP_ADMIN_PASSWORD not set.");
            return;
        }

        try
        {
            HeimdallUser? existing = await _userManager.FindByEmailAsync(email).ConfigureAwait(false);
            if (existing is null)
            {
                await CreateAdminAsync(email, password, cancellationToken).ConfigureAwait(false);
            }
            else if (existing.SystemAdmin)
            {
                // Fully-idempotent path: re-running on a row that's already an admin must
                // not write anything (no DB row touched, no audit event emitted). No PII
                // (email or email domain) is written to logs — the operator already knows
                // which email they configured via the env var.
                _logger.LogInformation(
                    "SystemAdmin bootstrap: target user already exists and is already an admin; no-op.");
            }
            else
            {
                await PromoteAdminAsync(existing, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Cooperative cancellation must propagate — host shutdown depends on it.
            throw;
        }
        catch (Exception ex)
        {
            // Bootstrap is best-effort by design: a transient DB outage at boot must
            // not abort startup. Log loudly and let the host continue.
            _logger.LogError(
                ex,
                "SystemAdmin bootstrap failed unexpectedly; continuing startup without bootstrap.");
        }
    }

    private async Task CreateAdminAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        // Set Id explicitly here. The migration's PK default gen_random_uuid() does not
        // fire because Identity always sends Id on INSERT; matches HeimdallUserStore.CreateAsync.
        var user = new HeimdallUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = _userManager.NormalizeEmail(email),
            EmailConfirmed = true,
            SystemAdmin = true,
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
        };

        IdentityResult result = await _userManager.CreateAsync(user, password).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            // Surface every IdentityError code+description so password-policy failures
            // (e.g. PasswordTooShort, PasswordRequiresDigit) are diagnosable in ops logs.
            string errors = string.Join(", ", result.Errors.Select(e => $"{e.Code}:{e.Description}"));
            _logger.LogError(
                "SystemAdmin bootstrap failed to create admin user. Identity errors: {IdentityErrors}",
                errors);
            return;
        }

        // Audit payload is intentionally empty — ActorUserId already identifies the
        // bootstrapped account, and writing the email or its domain into audit_events
        // would constitute a PII sink with no analytical benefit.
        await _auditWriter.WriteAsync(
            new AuditEvent
            {
                ActorUserId = user.Id,
                EventType = "bootstrap.admin.created",
                Target = user.Id.ToString(),
                Ip = null,
                UserAgent = null,
                PayloadJson = "{}",
            },
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "SystemAdmin bootstrap created admin user {UserId}.",
            user.Id);
    }

    private async Task PromoteAdminAsync(
        HeimdallUser existing,
        CancellationToken cancellationToken)
    {
        existing.SystemAdmin = true;

        // Use the IUserStore<HeimdallUser>.UpdateAsync interface method (rather than
        // casting to the concrete HeimdallUserStore) so this service is mockable at
        // the interface seam. The concrete store enforces optimistic concurrency via
        // its concurrency stamp.
        IdentityResult result = await _userStore.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            string errors = string.Join(", ", result.Errors.Select(e => $"{e.Code}:{e.Description}"));
            _logger.LogError(
                "SystemAdmin bootstrap failed to promote existing user {UserId}. Identity errors: {IdentityErrors}",
                existing.Id,
                errors);
            return;
        }

        await _auditWriter.WriteAsync(
            new AuditEvent
            {
                ActorUserId = existing.Id,
                EventType = "bootstrap.admin.promoted",
                Target = existing.Id.ToString(),
                Ip = null,
                UserAgent = null,
                PayloadJson = "{}",
            },
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "SystemAdmin bootstrap promoted user {UserId} to admin.",
            existing.Id);
    }
}
