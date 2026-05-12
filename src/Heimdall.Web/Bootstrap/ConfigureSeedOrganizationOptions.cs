using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Heimdall.Web.Bootstrap;

/// <summary>
/// <see cref="IConfigureOptions{TOptions}"/> implementation that resolves the
/// seed-organization id at first <see cref="IOptionsMonitor{TOptions}"/>
/// resolution. Resolution order: env var (<see cref="SeedOrganizationOptions.EnvVarName"/>)
/// or the equivalent <see cref="IConfiguration"/> key first, then any value
/// already published to <see cref="SeedOrganizationAccessor"/> (populated by
/// the post-startup DB-bootstrap path). Phase 4.6 step 15.
/// </summary>
public sealed class ConfigureSeedOrganizationOptions : IConfigureOptions<SeedOrganizationOptions>
{
    private readonly IConfiguration _configuration;
    private readonly SeedOrganizationAccessor _accessor;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="accessor">Mutable accessor populated by the bootstrapper.</param>
    /// <exception cref="ArgumentNullException">If any dependency is <c>null</c>.</exception>
    public ConfigureSeedOrganizationOptions(
        IConfiguration configuration,
        SeedOrganizationAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(accessor);
        _configuration = configuration;
        _accessor = accessor;
    }

    /// <inheritdoc />
    public void Configure(SeedOrganizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Env var first — operator-pinned value wins so a misconfigured DB
        // never silently overrides an explicit production choice.
        string? raw = _configuration[SeedOrganizationOptions.EnvVarName];
        if (!string.IsNullOrWhiteSpace(raw)
            && Guid.TryParse(raw, out Guid parsed)
            && parsed != Guid.Empty)
        {
            options.OrganizationId = parsed;
            return;
        }

        // Fallback: the value bootstrapping wrote into the accessor after
        // EnsureOrganizationAsync resolved the seed org. May still be null
        // on first Configure() call (during startup, before the bootstrapper
        // runs) — SeedOrganizationHealthProbe re-reads after bootstrap and
        // aborts if still unresolved.
        if (_accessor.OrganizationId is Guid resolved && resolved != Guid.Empty)
        {
            options.OrganizationId = resolved;
        }
    }
}
