using System;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.BLL.Authorization.OpenFga;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Heimdall.Web.Bootstrap;

/// <summary>
/// Optional startup runner that invokes <see cref="OpenFgaBackfillJob"/>, gated
/// behind the <c>HEIMDALL_OPENFGA_BACKFILL=1</c> env var. Phase 3.4 step 8.
/// </summary>
/// <remarks>
/// Failures are logged and swallowed: the backfill is idempotent and can be
/// retried on the next deploy by re-setting the env var, so a transient sidecar
/// outage during a rollout must not block the app from coming up.
/// </remarks>
public sealed class OpenFgaBackfillRunner
{
    /// <summary>Environment-variable name used to enable the backfill at startup.</summary>
    public const string EnableEnvVar = "HEIMDALL_OPENFGA_BACKFILL";

    private readonly IServiceProvider _services;
    private readonly ILogger<OpenFgaBackfillRunner> _logger;

    /// <summary>Initializes a new instance.</summary>
    public OpenFgaBackfillRunner(
        IServiceProvider services,
        ILogger<OpenFgaBackfillRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _logger = logger;
    }

    /// <summary>Runs the backfill if the env var is set to <c>1</c>; otherwise no-ops.</summary>
    /// <param name="cancellationToken">Host cancellation token.</param>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        string? flag = Environment.GetEnvironmentVariable(EnableEnvVar);
        if (!string.Equals(flag, "1", StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "OpenFGA backfill not enabled ({EnvVar}!=1); skipping.",
                EnableEnvVar);
            return;
        }

        OpenFgaBackfillJob? job = _services.GetService<OpenFgaBackfillJob>();
        if (job is null)
        {
            _logger.LogWarning(
                "OpenFGA backfill enabled but no backfill job is registered (sidecar not configured); skipping.");
            return;
        }

        try
        {
            OpenFgaBackfillJob.BackfillResult result = await job
                .RunAsync(cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "OpenFGA backfill runner finished. Result={@Result}",
                result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenFGA backfill runner failed; continuing startup.");
        }
    }
}
