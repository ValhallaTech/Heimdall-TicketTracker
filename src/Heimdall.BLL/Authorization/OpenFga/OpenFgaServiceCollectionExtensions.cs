using System;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenFga.Sdk.Client;
using OpenFga.Sdk.Configuration;

namespace Heimdall.BLL.Authorization.OpenFga;

/// <summary>
/// DI registration helpers for the OpenFGA SDK + adapter, per
/// <c>docs/proposals/openfga.md</c> §3 step 5.
/// </summary>
public static class OpenFgaServiceCollectionExtensions
{
    /// <summary>
    /// Environment-variable name for the OpenFGA HTTP API URL (matches <c>render.yaml</c>).
    /// </summary>
    public const string ApiUrlEnvVar = "OPENFGA_API_URL";

    /// <summary>Environment-variable name for the OpenFGA store id.</summary>
    public const string StoreIdEnvVar = "OPENFGA_STORE_ID";

    /// <summary>Environment-variable name for the pinned authorization-model id.</summary>
    public const string AuthorizationModelIdEnvVar = "OPENFGA_AUTHORIZATION_MODEL_ID";

    /// <summary>Environment-variable name for the optional pre-shared key.</summary>
    public const string PresharedKeyEnvVar = "OPENFGA_PRESHARED_KEY";

    /// <summary>Environment-variable name for the health-probe enable flag.</summary>
    public const string HealthProbeEnabledEnvVar = "OPENFGA_HEALTH_PROBE_ENABLED";

    /// <summary>
    /// Registers the OpenFGA SDK client, options, in-memory cache, and the
    /// <see cref="IOpenFgaAuthorizationService"/> + <see cref="ITupleWriter"/>
    /// implementations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bind order: <c>Authorization:OpenFga</c> from <see cref="IConfiguration"/>,
    /// then post-binder env-var overrides per the <c>render.yaml</c> contract.
    /// Env wins so the Render blueprint can flip values without redeploying app
    /// settings.
    /// </para>
    /// <para>
    /// Validation: <see cref="DataAnnotationValidateOptions{TOptions}"/>
    /// (<see cref="OptionsBuilderDataAnnotationsExtensions.ValidateDataAnnotations{TOptions}"/>)
    /// + <see cref="OptionsBuilderExtensions.ValidateOnStart{TOptions}"/> so a
    /// missing required value crashes startup loudly rather than degrading
    /// silently. Choice rationale: data-annotations + ValidateOnStart is the
    /// lowest-ceremony path; the contract is small (three required strings) and
    /// stable enough not to warrant a custom <see cref="IValidateOptions{TOptions}"/>.
    /// </para>
    /// </remarks>
    /// <param name="services">The DI service collection.</param>
    /// <param name="configuration">Application configuration root.</param>
    /// <returns>The <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddHeimdallOpenFga(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        OpenFgaOptions resolvedOptions = new();
        configuration.GetSection(OpenFgaOptions.SectionName).Bind(resolvedOptions);
        ApplyEnvironmentOverrides(resolvedOptions);

        OptionsBuilder<OpenFgaOptions> optionsBuilder = services
            .AddOptions<OpenFgaOptions>()
            .Bind(configuration.GetSection(OpenFgaOptions.SectionName))
            .Configure(ApplyEnvironmentOverrides);

        services.AddMemoryCache();

        bool sidecarConfigured =
            !string.IsNullOrWhiteSpace(resolvedOptions.ApiUrl)
            && !string.IsNullOrWhiteSpace(resolvedOptions.StoreId)
            && !string.IsNullOrWhiteSpace(resolvedOptions.AuthorizationModelId);

        if (!sidecarConfigured)
        {
            // No sidecar configured (typical for unit tests, ephemeral dev runs).
            // Register deny-closed no-op fall-backs so the rest of the BLL surface
            // can resolve normally without a live OpenFGA endpoint. The startup
            // probe is gated by HealthProbeEnabled and the backfill runner by an
            // env var, so neither will hit the SDK in this mode.
            //
            // Validation is intentionally NOT enabled here: the [Required] members
            // on OpenFgaOptions are empty by design in no-op mode, and turning on
            // ValidateDataAnnotations would crash startup for unconfigured envs.
            services.AddSingleton<IOpenFgaAuthorizationService, NoOpOpenFgaAuthorizationService>();
            services.AddSingleton<ITupleWriter, NoOpTupleWriter>();
            return services;
        }

        // Sidecar is configured — turn on data-annotations validation and fail
        // startup loudly if a required value is missing or whitespace.
        optionsBuilder
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // OpenFga SDK client — singleton; thread-safe per the SDK README.
        services.AddSingleton(provider =>
        {
            OpenFgaOptions options = provider
                .GetRequiredService<IOptions<OpenFgaOptions>>()
                .Value;

            ClientConfiguration clientConfig = new()
            {
                ApiUrl = options.ApiUrl,
                StoreId = options.StoreId,
                AuthorizationModelId = options.AuthorizationModelId,
            };

            if (!string.IsNullOrWhiteSpace(options.PresharedKey))
            {
                clientConfig.Credentials = new Credentials
                {
                    Method = CredentialsMethod.ApiToken,
                    Config = new CredentialsConfig
                    {
                        ApiToken = options.PresharedKey,
                    },
                };
            }

            // Owning the HttpClient here is acceptable for an app-lifetime singleton
            // talking to a single sidecar host (Render); the SDK itself does not
            // pool clients. Future change: swap to IHttpClientFactory if telemetry
            // / per-request handlers are required.
            return new OpenFgaClient(clientConfig, new HttpClient());
        });

        services.AddScoped<IOpenFgaAuthorizationService, OpenFgaAuthorizationService>();
        services.AddScoped<ITupleWriter, OpenFgaTupleWriter>();
        services.AddScoped<OpenFgaBackfillJob>();

        return services;
    }

    private static void ApplyEnvironmentOverrides(OpenFgaOptions options)
    {
        // Env-var post-binding per the render.yaml contract — env wins so the
        // Render blueprint can rotate the pre-shared key or the model id without
        // a config-file redeploy.
        string? apiUrl = Environment.GetEnvironmentVariable(ApiUrlEnvVar);
        if (!string.IsNullOrWhiteSpace(apiUrl))
        {
            options.ApiUrl = apiUrl;
        }

        string? storeId = Environment.GetEnvironmentVariable(StoreIdEnvVar);
        if (!string.IsNullOrWhiteSpace(storeId))
        {
            options.StoreId = storeId;
        }

        string? modelId = Environment.GetEnvironmentVariable(AuthorizationModelIdEnvVar);
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            options.AuthorizationModelId = modelId;
        }

        string? presharedKey = Environment.GetEnvironmentVariable(PresharedKeyEnvVar);
        if (!string.IsNullOrWhiteSpace(presharedKey))
        {
            options.PresharedKey = presharedKey;
        }

        string? healthProbeEnabled = Environment.GetEnvironmentVariable(
            HealthProbeEnabledEnvVar);
        if (!string.IsNullOrWhiteSpace(healthProbeEnabled)
            && bool.TryParse(healthProbeEnabled, out bool parsed))
        {
            options.HealthProbeEnabled = parsed;
        }
    }
}
