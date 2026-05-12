using System;

namespace Heimdall.Web.Bootstrap;

/// <summary>
/// Bound options carrying the seed organization's stable UUID. Per the
/// 2026-05-05 decision-log entry in
/// <c>docs/proposals/security-and-authorization.md</c>, the Phase 4.6 step 16
/// <c>RequireMfa</c> policy must key on the organization's stable id — never
/// its (admin-renamable) slug. Phase 4.6 step 15 of
/// <c>docs/implementation/phase-4-checklist.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// Resolution order, applied at startup via a <see cref="Microsoft.Extensions.Options.IConfigureOptions{TOptions}"/>:
/// </para>
/// <list type="number">
/// <item><description>
/// <c>HEIMDALL_SEED_ORGANIZATION_ID</c> environment variable, parsed as a
/// <see cref="Guid"/> — preferred for production so the operator pins the
/// value explicitly.
/// </description></item>
/// <item><description>
/// A DB lookup against <c>organizations WHERE slug = 'heimdall'</c> via
/// <see cref="Heimdall.Core.Interfaces.IOrganizationRepository.GetBySlugAsync"/>
/// — fallback for dev / first-boot.
/// </description></item>
/// </list>
/// <para>
/// If neither source resolves a value, <see cref="SeedOrganizationHealthProbe"/>
/// aborts startup — there is no silent "no admins need MFA" state.
/// </para>
/// </remarks>
public sealed class SeedOrganizationOptions
{
    /// <summary>
    /// Environment variable consulted (in addition to the equivalent
    /// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> key)
    /// to pin the seed organization id at startup.
    /// </summary>
    public const string EnvVarName = "HEIMDALL_SEED_ORGANIZATION_ID";

    /// <summary>
    /// The seed organization's stable UUID. <see cref="Guid.Empty"/> until
    /// <see cref="SeedOrganizationAccessor"/> resolves a real value.
    /// </summary>
    public Guid OrganizationId { get; set; }
}
