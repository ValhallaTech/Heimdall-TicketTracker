using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;

namespace Heimdall.Web.Tests.Endpoints;

/// <summary>
/// Phase 5.6 step 16 — integration tests pinning the OpenAPI document and
/// Swagger UI gating rules in <c>Program.cs</c> (lines 975-1005).
/// </summary>
/// <remarks>
/// <para>
/// The production gating matrix the suite locks in:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <b>Development + <c>Api:Documentation:Enabled</c>=true</b>: both
///       <c>/api/v1/openapi.json</c> and <c>/api/v1/docs/index.html</c> serve 200.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Production + <c>Api:Documentation:Enabled</c>=true</b>:
///       defense-in-depth (<c>!IsProduction()</c> guard plus Development-only UI
///       branch) blocks both endpoints; both return 404.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Staging + <c>Api:Documentation:Enabled</c>=false</b>: flag-off
///       default blocks the document; the UI is Development-only. Both 404.
///     </description>
///   </item>
/// </list>
/// <para>
/// Each environment variant uses its own dedicated factory (with its own
/// Postgres testcontainer) because <c>Program.cs</c> validates
/// <c>DATABASE_URL</c> and reads the environment at host build, not on first
/// request. The base <c>HeimdallWebApplicationFactory</c> is sealed, so each
/// variant subclasses <see cref="WebApplicationFactory{TEntryPoint}"/> directly
/// and reproduces just enough of the base wiring (env vars + Postgres) to boot.
/// </para>
/// </remarks>
public abstract class OpenApiGatingFactoryBase
    : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("heimdall_openapi_gating")
        .WithUsername("heimdall")
        .WithPassword("heimdall_test")
        .Build();

    protected abstract string AspNetCoreEnvironment { get; }

    protected abstract string DocumentationEnabledValue { get; }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync().ConfigureAwait(false);
    }

    public new async Task DisposeAsync()
    {
        await _postgres.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        Environment.SetEnvironmentVariable("DATABASE_URL", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("REDIS_URL", "localhost:6379");
        Environment.SetEnvironmentVariable("SEED_DATABASE", "false");
        Environment.SetEnvironmentVariable("HEIMDALL_BOOTSTRAP_ADMIN_EMAIL", string.Empty);
        Environment.SetEnvironmentVariable("HEIMDALL_BOOTSTRAP_ADMIN_PASSWORD", string.Empty);
        // Production's SeedOrganizationHealthProbe (Program.cs:816) refuses to
        // start unless either the 'heimdall' organization exists or
        // HEIMDALL_SEED_ORGANIZATION_ID resolves to a UUID. SEED_DATABASE=false
        // skips seeding, so provide a deterministic UUID for the probe.
        Environment.SetEnvironmentVariable(
            "HEIMDALL_SEED_ORGANIZATION_ID",
            "00000000-0000-0000-0000-00000000abcd");
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(AspNetCoreEnvironment);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:Documentation:Enabled"] = DocumentationEnabledValue,
            });
        });
    }
}

public sealed class OpenApiGatingDevelopmentEnabledFactory : OpenApiGatingFactoryBase
{
    protected override string AspNetCoreEnvironment => "Development";

    protected override string DocumentationEnabledValue => "true";
}

public sealed class OpenApiGatingProductionEnabledFactory : OpenApiGatingFactoryBase
{
    protected override string AspNetCoreEnvironment => "Production";

    protected override string DocumentationEnabledValue => "true";
}

public sealed class OpenApiGatingStagingDisabledFactory : OpenApiGatingFactoryBase
{
    protected override string AspNetCoreEnvironment => "Staging";

    protected override string DocumentationEnabledValue => "false";
}

[Collection("Phase5OpenApi")]
public sealed class OpenApiGatingTests_DevelopmentEnabled
    : IClassFixture<OpenApiGatingDevelopmentEnabledFactory>
{
    private readonly OpenApiGatingDevelopmentEnabledFactory _factory;

    public OpenApiGatingTests_DevelopmentEnabled(OpenApiGatingDevelopmentEnabledFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OpenApiDocument_DevelopmentEnabled_Returns200()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/api/v1/openapi.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "Development + Api:Documentation:Enabled=true is the canonical 'docs on' configuration.");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SwaggerUi_DevelopmentEnabled_Returns200()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/api/v1/docs/index.html");
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "Swagger UI is mapped under /api/v1/docs whenever the environment is Development.");
    }
}

[Collection("Phase5OpenApi")]
public sealed class OpenApiGatingTests_ProductionEnabled
    : IClassFixture<OpenApiGatingProductionEnabledFactory>
{
    private readonly OpenApiGatingProductionEnabledFactory _factory;

    public OpenApiGatingTests_ProductionEnabled(OpenApiGatingProductionEnabledFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OpenApiDocument_ProductionEnabled_Returns404()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/api/v1/openapi.json");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            because: "Production must never serve the OpenAPI document — Program.cs explicitly excludes IsProduction() "
                + "as defense-in-depth against an Api:Documentation:Enabled=true value leaking into a prod config.");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SwaggerUi_ProductionEnabled_Returns404()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/api/v1/docs/index.html");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            because: "Swagger UI is gated on IsDevelopment() only; it must never serve in Production.");
    }
}

[Collection("Phase5OpenApi")]
public sealed class OpenApiGatingTests_StagingDisabled
    : IClassFixture<OpenApiGatingStagingDisabledFactory>
{
    private readonly OpenApiGatingStagingDisabledFactory _factory;

    public OpenApiGatingTests_StagingDisabled(OpenApiGatingStagingDisabledFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OpenApiDocument_StagingDisabled_Returns404()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/api/v1/openapi.json");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            because: "Api:Documentation:Enabled=false suppresses the openapi route in every non-Development environment.");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SwaggerUi_StagingDisabled_Returns404()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/api/v1/docs/index.html");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            because: "Swagger UI is Development-only regardless of the Api:Documentation:Enabled flag.");
    }
}
