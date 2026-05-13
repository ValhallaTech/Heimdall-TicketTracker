using System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Heimdall.Web.Bootstrap;

/// <summary>
/// Startup probe that asserts a seed-organization id was successfully resolved
/// by <see cref="SeedOrganizationAccessor"/> (env var or DB lookup). Phase 4.6
/// step 15 of <c>docs/implementation/phase-4-checklist.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// A missing seed-org id would silently disable the Phase 4.6 step 16
/// <c>RequireMfa</c> check (the handler would resolve <see cref="Guid.Empty"/>
/// as its organization id and every FGA <c>admin@organization</c> probe would
/// return <c>false</c>, leaving the admin pages reachable without MFA). In
/// Production the probe aborts startup so the operator cannot deploy in that
/// state; in non-Production environments it logs a warning and lets the host
/// come up so dev/acceptance loops are not blocked by an unseeded database.
/// </para>
/// <para>
/// Implementation note: the probe rebuilds the <see cref="SeedOrganizationOptions"/>
/// snapshot via <see cref="IOptionsFactory{TOptions}"/> and publishes it to
/// <see cref="IOptionsMonitorCache{TOptions}"/> so subsequent
/// <see cref="IOptionsMonitor{TOptions}.CurrentValue"/> reads (from
/// <c>RequireMfaAuthorizationHandler</c>) observe the post-bootstrap value.
/// Without this, the monitor would cache an empty snapshot taken before the
/// bootstrapper ran.
/// </para>
/// </remarks>
public sealed class SeedOrganizationHealthProbe
{
    private readonly IOptionsFactory<SeedOrganizationOptions> _factory;
    private readonly IOptionsMonitorCache<SeedOrganizationOptions> _cache;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<SeedOrganizationHealthProbe> _logger;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="factory">Options factory used to build a fresh snapshot
    /// reflecting the current <see cref="SeedOrganizationAccessor"/> state.</param>
    /// <param name="cache">Monitor cache used to publish the rebuilt snapshot
    /// so downstream <see cref="IOptionsMonitor{TOptions}"/> consumers see it.</param>
    /// <param name="environment">Host environment; controls whether a missing
    /// seed-org id is fatal (Production) or warn-only (Development / test).</param>
    /// <param name="logger">Structured logger.</param>
    /// <exception cref="ArgumentNullException">If any dependency is <c>null</c>.</exception>
    public SeedOrganizationHealthProbe(
        IOptionsFactory<SeedOrganizationOptions> factory,
        IOptionsMonitorCache<SeedOrganizationOptions> cache,
        IHostEnvironment environment,
        ILogger<SeedOrganizationHealthProbe> logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _cache = cache;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>Runs the probe.</summary>
    /// <returns>The resolved seed-organization id, or <see cref="Guid.Empty"/>
    /// in non-Production environments when no id was resolved.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown only in <see cref="Environments.Production"/> when no
    /// seed-organization id was resolved. Crashing startup is intentional in
    /// that case.
    /// </exception>
    public Guid Run()
    {
        // Force a fresh Configure() pass. IConfigureOptions reads env-var and
        // accessor, both of which may have changed between service-collection
        // build and the post-bootstrap point at which this probe runs.
        _cache.TryRemove(Options.DefaultName);
        SeedOrganizationOptions snapshot = _factory.Create(Options.DefaultName);
        _cache.TryAdd(Options.DefaultName, snapshot);

        Guid organizationId = snapshot.OrganizationId;
        if (organizationId == Guid.Empty)
        {
            if (_environment.IsProduction())
            {
                _logger.LogCritical(
                    "Seed-organization id was not resolved at startup (env var {EnvVar} unset and DB lookup did not populate the accessor); aborting.",
                    SeedOrganizationOptions.EnvVarName);
                throw new InvalidOperationException(
                    $"Seed-organization id was not resolved. Set {SeedOrganizationOptions.EnvVarName} to a valid UUID, or ensure the 'heimdall' organization exists so {nameof(DefaultHierarchyBootstrapper)} can populate the accessor.");
            }

            // Non-Production (Development, test factory, etc.): RequireMfa
            // will deny admin pages until the seed org is seeded, which is
            // the correct safety posture. Don't block boot.
            _logger.LogWarning(
                "Seed-organization id not resolved in {Environment}; admin pages remain blocked until {EnvVar} is set or the 'heimdall' organization is seeded.",
                _environment.EnvironmentName,
                SeedOrganizationOptions.EnvVarName);
            return Guid.Empty;
        }

        _logger.LogInformation(
            "Seed-organization id resolved. OrganizationId={OrganizationId}",
            organizationId);
        return organizationId;
    }
}

