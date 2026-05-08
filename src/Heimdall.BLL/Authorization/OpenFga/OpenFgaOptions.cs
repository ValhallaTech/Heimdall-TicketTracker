using System;
using System.ComponentModel.DataAnnotations;

namespace Heimdall.BLL.Authorization.OpenFga;

/// <summary>
/// Strongly-typed binding for the <c>Authorization:OpenFga</c> configuration section,
/// per <c>docs/proposals/openfga.md</c> §3 step 5 (Heimdall.Web SDK integration) and
/// the env-var contract in <c>render.yaml</c> (<c>OPENFGA_API_URL</c> /
/// <c>OPENFGA_STORE_ID</c> / <c>OPENFGA_AUTHORIZATION_MODEL_ID</c> /
/// <c>OPENFGA_PRESHARED_KEY</c>).
/// </summary>
/// <remarks>
/// The cache TTL bounds the freshness window of the in-process check cache used by
/// <see cref="OpenFgaAuthorizationService"/>. Per
/// <c>docs/proposals/security-and-authorization.md</c> §9.2 the cache is process-wide
/// with a small TTL, <strong>not</strong> circuit-scoped — cached entries can be served
/// across requests on the same instance until the TTL elapses or the entry is evicted
/// by memory pressure.
/// </remarks>
public sealed class OpenFgaOptions
{
    /// <summary>
    /// Configuration section name (<c>Authorization:OpenFga</c>). Stable contract for
    /// <see cref="Microsoft.Extensions.Configuration.IConfiguration.GetSection(string)"/> consumers.
    /// </summary>
    public const string SectionName = "Authorization:OpenFga";

    /// <summary>
    /// Gets or sets the OpenFGA HTTP API base URL (e.g. <c>http://openfga:8080</c>).
    /// Sourced from <c>OPENFGA_API_URL</c> in production per <c>render.yaml</c>.
    /// </summary>
    [Required]
    public string ApiUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the OpenFGA store id this app instance binds to. Sourced from
    /// <c>OPENFGA_STORE_ID</c> in production per <c>render.yaml</c>.
    /// </summary>
    [Required]
    public string StoreId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the authorization-model id this app instance pins reads/writes to.
    /// Sourced from <c>OPENFGA_AUTHORIZATION_MODEL_ID</c> in production per
    /// <c>render.yaml</c>. Pinning the model id is required so the app and the model
    /// roll forward together — a mismatch surfaces as a fast 400 from the sidecar.
    /// </summary>
    [Required]
    public string AuthorizationModelId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional pre-shared key used as a bearer token by the OpenFGA
    /// sidecar (<c>OPENFGA_PRESHARED_KEY</c>). When <c>null</c> or empty the SDK is
    /// configured with no auth (development / local stacks); when set it is wired as
    /// <see cref="OpenFga.Sdk.Configuration.CredentialsMethod.ApiToken"/>.
    /// </summary>
    public string? PresharedKey { get; set; }

    /// <summary>
    /// Gets or sets the in-process check-cache TTL. The cache is process-wide and
    /// bounded by this TTL — <strong>not</strong> circuit-scoped — per
    /// <c>docs/proposals/security-and-authorization.md</c> §9.2. Default 2 seconds:
    /// long enough to coalesce per-page bursts, short enough that a freshly-written
    /// tuple is observed in well under a request-second.
    /// </summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets or sets the timeout used by <see cref="Heimdall.Web.Bootstrap"/>'s
    /// startup health probe to confirm the sidecar is reachable. Per
    /// <c>docs/proposals/openfga.md</c> §3 step 5 the probe must fail-fast on an
    /// unreachable sidecar so the app does not boot into a deny-everything state
    /// silently. Default 5 seconds.
    /// </summary>
    public TimeSpan HealthProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets a flag that, when <c>true</c>, requires the startup health probe to
    /// succeed; when <c>false</c> the probe logs and returns. Defaults to <c>false</c>
    /// so dev / test workflows boot without an OpenFGA sidecar; the Render blueprint
    /// flips it to <c>true</c> in production via <c>OPENFGA_HEALTH_PROBE_ENABLED=true</c>
    /// per <c>docs/runbooks/openfga-bootstrap.md</c>.
    /// </summary>
    public bool HealthProbeEnabled { get; set; }
}
