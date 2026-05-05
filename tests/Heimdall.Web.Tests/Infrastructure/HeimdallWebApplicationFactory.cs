using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace Heimdall.Web.Tests.Infrastructure;

/// <summary>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> wrapper that boots a real
/// <c>Heimdall.Web</c> host against a Testcontainers Postgres container so the
/// Phase 1 acceptance suite can drive the full HTTP pipeline (cookie auth,
/// antiforgery, rate limiting, audit-event persistence) end-to-end.
/// </summary>
/// <remarks>
/// <para>
/// <c>DATABASE_URL</c>, <c>REDIS_URL</c>, and <c>SEED_DATABASE</c> are exported
/// before <see cref="WebApplicationFactory{TEntryPoint}.CreateHost(IHostBuilder)"/>
/// runs so <c>Program.cs</c> picks them up at the same point production startup
/// reads them. <c>SEED_DATABASE=false</c> keeps the acceptance database empty so
/// each test fixture starts deterministic.
/// </para>
/// <para>
/// Redis is intentionally <em>not</em> stubbed: the production multiplexer is
/// configured with <c>AbortOnConnectFail=false</c>, so a missing Redis server is
/// downgraded to cache misses rather than a startup failure. If a future change
/// makes Redis required at boot, <see cref="WebApplicationFactory{TEntryPoint}.ConfigureWebHost"/>
/// is the seam to swap in a Moq-based <c>IConnectionMultiplexer</c>.
/// </para>
/// </remarks>
public sealed class HeimdallWebApplicationFactory
    : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("heimdall_acceptance")
        .WithUsername("heimdall")
        .WithPassword("heimdall_test")
        .Build();

    /// <summary>
    /// Gets the connection string for the Testcontainers Postgres instance backing
    /// this factory. Only valid after <see cref="InitializeAsync"/> completes.
    /// </summary>
    public string ConnectionString => _postgres.GetConnectionString();

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public new async Task DisposeAsync()
    {
        await _postgres.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override IHost CreateHost(IHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Set env vars BEFORE host creation so Program.cs picks them up at the
        // same point production startup reads them.
        Environment.SetEnvironmentVariable("DATABASE_URL", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("REDIS_URL", "localhost:6379");
        Environment.SetEnvironmentVariable("SEED_DATABASE", "false");
        Environment.SetEnvironmentVariable("HEIMDALL_BOOTSTRAP_ADMIN_EMAIL", string.Empty);
        Environment.SetEnvironmentVariable("HEIMDALL_BOOTSTRAP_ADMIN_PASSWORD", string.Empty);
        return base.CreateHost(builder);
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
    }
}
