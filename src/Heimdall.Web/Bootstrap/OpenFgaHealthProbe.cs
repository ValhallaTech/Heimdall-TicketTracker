using System;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.BLL.Authorization.OpenFga;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenFga.Sdk.Client;
using OpenFga.Sdk.Client.Model;

namespace Heimdall.Web.Bootstrap;

/// <summary>
/// Optional startup health probe that validates the configured OpenFGA sidecar +
/// store + authorization-model triplet by issuing a single
/// <see cref="OpenFgaClient.ReadAuthorizationModel(IClientReadAuthorizationModelOptions, System.Threading.CancellationToken)"/>
/// call inside <see cref="OpenFgaOptions.HealthProbeTimeout"/>.
/// </summary>
/// <remarks>
/// <para>
/// Gating: only runs when <see cref="OpenFgaOptions.HealthProbeEnabled"/> is
/// <c>true</c> (also overridable via <c>OPENFGA_HEALTH_PROBE_ENABLED</c>). When
/// enabled, a probe failure aborts startup — that is the whole point of the
/// probe. When disabled, the method is a no-op so dev environments and
/// pre-cutover deploys do not need a reachable sidecar.
/// </para>
/// <para>
/// <see cref="OpenFgaClient.ReadAuthorizationModel(IClientReadAuthorizationModelOptions, System.Threading.CancellationToken)"/>
/// is preferred over a bare <c>ListStores</c> because it actually exercises the
/// configured <see cref="OpenFgaOptions.StoreId"/> / <see cref="OpenFgaOptions.AuthorizationModelId"/>
/// binding — which is precisely the misconfiguration most likely to slip past
/// CI.
/// </para>
/// </remarks>
public sealed class OpenFgaHealthProbe
{
    private readonly IServiceProvider _services;
    private readonly OpenFgaOptions _options;
    private readonly ILogger<OpenFgaHealthProbe> _logger;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="services">Root service provider used to lazy-resolve the
    /// SDK client only when the probe is enabled and a sidecar has been
    /// configured.</param>
    /// <param name="options">Bound <see cref="OpenFgaOptions"/>.</param>
    /// <param name="logger">Structured logger.</param>
    /// <exception cref="ArgumentNullException">If any required dependency is <c>null</c>.</exception>
    public OpenFgaHealthProbe(
        IServiceProvider services,
        IOptions<OpenFgaOptions> options,
        ILogger<OpenFgaHealthProbe> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Runs the health probe.</summary>
    /// <param name="cancellationToken">Host cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the probe is enabled and the call to OpenFGA fails. Crashing
    /// startup is the intended behaviour — it forces operators to fix
    /// configuration before traffic is served.
    /// </exception>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!_options.HealthProbeEnabled)
        {
            _logger.LogInformation("OpenFGA health probe disabled; skipping.");
            return;
        }

        OpenFgaClient? client = _services.GetService<OpenFgaClient>();
        if (client is null)
        {
            // Probe was enabled but no SDK client is registered — that means
            // AddHeimdallOpenFga decided the sidecar is not configured. Treat
            // this as a misconfiguration: the operator asked for the probe but
            // the app cannot reach OpenFGA at all.
            _logger.LogCritical(
                "OpenFGA health probe enabled but no OpenFGA client is registered (ApiUrl/StoreId/ModelId not configured); aborting startup.");
            throw new InvalidOperationException(
                "OpenFGA health probe enabled but the SDK client is not configured. Set OPENFGA_API_URL, OPENFGA_STORE_ID, and OPENFGA_AUTHORIZATION_MODEL_ID.");
        }

        _logger.LogInformation(
            "OpenFGA health probe starting. ApiUrl={ApiUrl} StoreId={StoreId} ModelId={ModelId} Timeout={Timeout}",
            _options.ApiUrl,
            _options.StoreId,
            _options.AuthorizationModelId,
            _options.HealthProbeTimeout);

        using CancellationTokenSource timeoutCts = new(_options.HealthProbeTimeout);
        using CancellationTokenSource linkedCts = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await client
                .ReadAuthorizationModel(options: null, linkedCts.Token)
                .ConfigureAwait(false);

            _logger.LogInformation("OpenFGA health probe succeeded.");
        }
        catch (OperationCanceledException ex)
            when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogCritical(
                ex,
                "OpenFGA health probe timed out after {Timeout}.",
                _options.HealthProbeTimeout);
            throw new InvalidOperationException(
                $"OpenFGA health probe timed out after {_options.HealthProbeTimeout}.",
                ex);
        }
        catch (OperationCanceledException)
        {
            // Host shutdown — propagate.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(
                ex,
                "OpenFGA health probe failed. ApiUrl={ApiUrl} StoreId={StoreId} ModelId={ModelId}",
                _options.ApiUrl,
                _options.StoreId,
                _options.AuthorizationModelId);
            throw new InvalidOperationException(
                "OpenFGA health probe failed; aborting startup.",
                ex);
        }
    }
}
