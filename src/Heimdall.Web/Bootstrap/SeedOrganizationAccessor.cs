using System;

namespace Heimdall.Web.Bootstrap;

/// <summary>
/// Mutable singleton holder for the resolved seed-organization id, written by
/// startup wiring (env-var first, then <see cref="DefaultHierarchyBootstrapper"/>
/// DB lookup) and read by <see cref="SeedOrganizationOptions"/> via an
/// <see cref="Microsoft.Extensions.Options.IConfigureOptions{TOptions}"/>.
/// Phase 4.6 step 15 of <c>docs/implementation/phase-4-checklist.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// The accessor is a singleton because the seed-organization id is fixed for
/// the lifetime of the process: it is resolved exactly once at startup and
/// never changes thereafter. Reads after startup are uncontended.
/// </para>
/// <para>
/// Volatility is deliberate. Two readers may legitimately race a startup
/// writer (the bootstrapper publishes after a fresh-DB org create); volatile
/// semantics give the reader a publication guarantee without a lock. Once
/// the bootstrapper sets the value, every subsequent read observes it.
/// </para>
/// </remarks>
public sealed class SeedOrganizationAccessor
{
    private readonly object _gate = new();
    private Guid _organizationId;

    /// <summary>
    /// The resolved seed-organization id, or <c>null</c> until startup
    /// wiring has populated it.
    /// </summary>
    public Guid? OrganizationId
    {
        get
        {
            lock (_gate)
            {
                return _organizationId == Guid.Empty ? null : _organizationId;
            }
        }

        set
        {
            lock (_gate)
            {
                _organizationId = value ?? Guid.Empty;
            }
        }
    }
}
