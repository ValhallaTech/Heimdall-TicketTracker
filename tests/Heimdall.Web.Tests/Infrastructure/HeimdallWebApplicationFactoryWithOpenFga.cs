using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Heimdall.Tests.Shared.OpenFga;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace Heimdall.Web.Tests.Infrastructure;

/// <summary>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> variant that boots the full
/// <c>Heimdall.Web</c> host wired to both a Testcontainers Postgres instance and
/// an <see cref="OpenFgaTestcontainersFixture"/> sidecar. Intended for
/// Phase&nbsp;3 acceptance tests that exercise the end-to-end OpenFGA
/// authorization pipeline (resource checks, tuple-backed access control).
/// </summary>
/// <remarks>
/// <para>
/// <c>DATABASE_URL</c>, <c>REDIS_URL</c>, <c>SEED_DATABASE</c>, and all four
/// <c>OPENFGA_*</c> environment variables are set before
/// <see cref="WebApplicationFactory{TEntryPoint}.CreateHost(IHostBuilder)"/>
/// runs so <c>Program.cs</c> reads them at the same point production startup
/// does. <c>Authorization:Provider</c> is injected via
/// <c>IWebHostBuilder.ConfigureAppConfiguration</c> so the Autofac module
/// selects <see cref="Heimdall.BLL.Authorization.OpenFga.OpenFgaPermissionService"/>
/// rather than the default <c>TeamRole</c> service.
/// </para>
/// <para>
/// The heavy container boot is guarded by
/// <c>HEIMDALL_OPENFGA_TESTS_ENABLED</c>: when that variable is <c>false</c>
/// or <c>0</c>, <see cref="InitializeAsync"/> skips Docker startup so test
/// infrastructure without Docker does not fail at fixture-creation time. Tests
/// that depend on this factory should be decorated with
/// <c>[OpenFgaIntegrationFact]</c> so they skip gracefully in the same
/// environments.
/// </para>
/// <para>
/// Redis is not stubbed: the production <c>AbortOnConnectFail=false</c>
/// multiplexer degrades to cache misses gracefully when no Redis server is
/// reachable.
/// </para>
/// </remarks>
public sealed class HeimdallWebApplicationFactoryWithOpenFga
    : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("heimdall_phase3_acceptance")
        .WithUsername("heimdall")
        .WithPassword("heimdall_test")
        .Build();

    private readonly OpenFgaTestcontainersFixture _openFga = new();

    private bool _started;

    /// <summary>
    /// Gets the connection string for the app's Testcontainers Postgres.
    /// Only valid after <see cref="InitializeAsync"/> completes.
    /// </summary>
    public string ConnectionString => _postgres.GetConnectionString();

    /// <summary>
    /// Gets the <see cref="OpenFgaTestcontainersFixture"/> used by this factory.
    /// Provides <see cref="OpenFgaTestcontainersFixture.CreateSdkClient()"/> for
    /// seeding tuples directly from tests.
    /// Only valid after <see cref="InitializeAsync"/> completes.
    /// </summary>
    public OpenFgaTestcontainersFixture OpenFga => _openFga;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        string? value = Environment.GetEnvironmentVariable(
            OpenFgaTestcontainersFixture.EnabledEnvVar);

        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "0", StringComparison.Ordinal))
        {
            // Containers are not started — skip flag guards DisposeAsync too.
            return;
        }

        await _postgres.StartAsync().ConfigureAwait(false);
        await _openFga.InitializeAsync().ConfigureAwait(false);
        _started = true;
    }

    /// <inheritdoc />
    public new async Task DisposeAsync()
    {
        if (_started)
        {
            await _openFga.DisposeAsync().ConfigureAwait(false);
            await _postgres.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override IHost CreateHost(IHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Core app infrastructure (mirrors HeimdallWebApplicationFactory).
        Environment.SetEnvironmentVariable("DATABASE_URL", _started ? _postgres.GetConnectionString() : string.Empty);
        Environment.SetEnvironmentVariable("REDIS_URL", "localhost:6379");
        Environment.SetEnvironmentVariable("SEED_DATABASE", "false");
        Environment.SetEnvironmentVariable("HEIMDALL_BOOTSTRAP_ADMIN_EMAIL", string.Empty);
        Environment.SetEnvironmentVariable("HEIMDALL_BOOTSTRAP_ADMIN_PASSWORD", string.Empty);

        // OpenFGA sidecar — env-var contract per render.yaml + OpenFgaServiceCollectionExtensions.
        Environment.SetEnvironmentVariable("OPENFGA_API_URL", _started ? _openFga.ApiUrl : string.Empty);
        Environment.SetEnvironmentVariable("OPENFGA_STORE_ID", _started ? _openFga.StoreId : string.Empty);
        Environment.SetEnvironmentVariable("OPENFGA_AUTHORIZATION_MODEL_ID", _started ? _openFga.AuthorizationModelId : string.Empty);
        Environment.SetEnvironmentVariable("OPENFGA_PRESHARED_KEY", _started ? _openFga.PresharedKey : string.Empty);

        return base.CreateHost(builder);
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");

        // Override Authorization:Provider to OpenFga so the Autofac module
        // wires OpenFgaPermissionService instead of the default TeamRole service.
        builder.ConfigureAppConfiguration(configBuilder =>
        {
            configBuilder.AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("Authorization:Provider", "OpenFga"),
            });
        });
    }
}
