using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.BLL.Tests.Authorization.OpenFga.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Heimdall.BLL.Tests.Authorization.OpenFga;

/// <summary>
/// Unit tests for <see cref="OpenFgaServiceCollectionExtensions.AddHeimdallOpenFga"/>:
/// covers the deny-closed fall-back path, the live-sidecar path, env-var override
/// precedence, and option binding.
/// </summary>
[Collection(EnvironmentVariableSerialCollection.Name)]
public class OpenFgaServiceCollectionExtensionsTests
{
    private static EnvironmentVariableScope ClearAll(
        IReadOnlyDictionary<string, string?>? extra = null)
    {
        var map = new Dictionary<string, string?>
        {
            [OpenFgaServiceCollectionExtensions.ApiUrlEnvVar] = null,
            [OpenFgaServiceCollectionExtensions.StoreIdEnvVar] = null,
            [OpenFgaServiceCollectionExtensions.AuthorizationModelIdEnvVar] = null,
            [OpenFgaServiceCollectionExtensions.PresharedKeyEnvVar] = null,
            [OpenFgaServiceCollectionExtensions.HealthProbeEnabledEnvVar] = null,
        };
        if (extra is not null)
        {
            foreach (KeyValuePair<string, string?> kvp in extra)
            {
                map[kvp.Key] = kvp.Value;
            }
        }

        return new EnvironmentVariableScope(map);
    }

    private static ServiceProvider Build(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();
        services.AddHeimdallOpenFga(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddHeimdallOpenFga_Should_Throw_When_ServicesIsNull()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        Action act = () => OpenFgaServiceCollectionExtensions.AddHeimdallOpenFga(
            null!,
            configuration);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddHeimdallOpenFga_Should_Throw_When_ConfigurationIsNull()
    {
        var services = new ServiceCollection();
        Action act = () => services.AddHeimdallOpenFga(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddHeimdallOpenFga_Should_RegisterNoOpServices_When_NoSidecarConfigured()
    {
        using var env = ClearAll();
        IConfiguration configuration = new ConfigurationBuilder().Build();

        using ServiceProvider sp = Build(configuration);

        sp.GetRequiredService<IOpenFgaAuthorizationService>()
            .GetType().Name.Should().Be("NoOpOpenFgaAuthorizationService");
        sp.GetRequiredService<ITupleWriter>()
            .GetType().Name.Should().Be("NoOpTupleWriter");
    }

    [Fact]
    public void AddHeimdallOpenFga_Should_RegisterNoOpServices_When_OnlyTwoOfThreeRequiredAreSet()
    {
        using var env = ClearAll(new Dictionary<string, string?>
        {
            [OpenFgaServiceCollectionExtensions.ApiUrlEnvVar] = "http://openfga:8080",
            [OpenFgaServiceCollectionExtensions.StoreIdEnvVar] = "01HQ7Z9P0KQH7N7E5K7H8GQK3M",
            // AuthorizationModelId intentionally missing
        });
        IConfiguration configuration = new ConfigurationBuilder().Build();

        using ServiceProvider sp = Build(configuration);

        sp.GetRequiredService<IOpenFgaAuthorizationService>()
            .GetType().Name.Should().Be("NoOpOpenFgaAuthorizationService");
    }

    [Fact]
    public void AddHeimdallOpenFga_Should_RegisterRealServices_When_AllRequiredSetViaEnv()
    {
        using var env = ClearAll(new Dictionary<string, string?>
        {
            [OpenFgaServiceCollectionExtensions.ApiUrlEnvVar] = "http://openfga:8080",
            [OpenFgaServiceCollectionExtensions.StoreIdEnvVar] = "01HQ7Z9P0KQH7N7E5K7H8GQK3M",
            [OpenFgaServiceCollectionExtensions.AuthorizationModelIdEnvVar] = "01HQ7Z9P0KQH7N7E5K7H8GQK3N",
        });
        IConfiguration configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();

        services.AddHeimdallOpenFga(configuration);

        // Inspect registrations only — resolving the real OpenFGA services would
        // require a live audit-event writer + repository graph, which is out of
        // scope for this unit test.
        ServiceDescriptor authzDescriptor = services
            .Single(d => d.ServiceType == typeof(IOpenFgaAuthorizationService));
        authzDescriptor.ImplementationType.Should().NotBeNull();
        authzDescriptor.ImplementationType!.Name.Should().NotStartWith("NoOp");
        authzDescriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);

        ServiceDescriptor tupleDescriptor = services
            .Single(d => d.ServiceType == typeof(ITupleWriter));
        tupleDescriptor.ImplementationType.Should().NotBeNull();
        tupleDescriptor.ImplementationType!.Name.Should().NotStartWith("NoOp");
        tupleDescriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);

        services.Should().Contain(d => d.ServiceType == typeof(OpenFgaBackfillJob));
    }

    [Fact]
    public void AddHeimdallOpenFga_Should_PreferEnvVarOverConfiguration_When_BothAreSet()
    {
        using var env = ClearAll(new Dictionary<string, string?>
        {
            [OpenFgaServiceCollectionExtensions.ApiUrlEnvVar] = "http://from-env:8080",
            [OpenFgaServiceCollectionExtensions.StoreIdEnvVar] = "01HQ7Z9P0KQH7N7E5K7H8GQK3M",
            [OpenFgaServiceCollectionExtensions.AuthorizationModelIdEnvVar] = "01HQ7Z9P0KQH7N7E5K7H8GQK3N",
        });
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authorization:OpenFga:ApiUrl"] = "http://from-config:8080",
                ["Authorization:OpenFga:StoreId"] = "config-store",
                ["Authorization:OpenFga:AuthorizationModelId"] = "config-model",
            })
            .Build();

        using ServiceProvider sp = Build(configuration);

        OpenFgaOptions resolved = sp.GetRequiredService<IOptions<OpenFgaOptions>>().Value;
        resolved.ApiUrl.Should().Be("http://from-env:8080");
        resolved.StoreId.Should().Be("01HQ7Z9P0KQH7N7E5K7H8GQK3M");
        resolved.AuthorizationModelId.Should().Be("01HQ7Z9P0KQH7N7E5K7H8GQK3N");
    }

    [Fact]
    public void AddHeimdallOpenFga_Should_BindCacheTtl_From_ConfigurationSection()
    {
        using var env = ClearAll();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authorization:OpenFga:CacheTtl"] = "00:00:07",
                ["Authorization:OpenFga:HealthProbeTimeout"] = "00:00:09",
            })
            .Build();

        using ServiceProvider sp = Build(configuration);

        OpenFgaOptions resolved = sp.GetRequiredService<IOptions<OpenFgaOptions>>().Value;
        resolved.CacheTtl.Should().Be(TimeSpan.FromSeconds(7));
        resolved.HealthProbeTimeout.Should().Be(TimeSpan.FromSeconds(9));
    }

    [Fact]
    public void AddHeimdallOpenFga_Should_OverrideHealthProbeEnabled_From_EnvVar()
    {
        using var env = ClearAll(new Dictionary<string, string?>
        {
            [OpenFgaServiceCollectionExtensions.HealthProbeEnabledEnvVar] = "true",
        });
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authorization:OpenFga:HealthProbeEnabled"] = "false",
            })
            .Build();

        using ServiceProvider sp = Build(configuration);

        OpenFgaOptions resolved = sp.GetRequiredService<IOptions<OpenFgaOptions>>().Value;
        resolved.HealthProbeEnabled.Should().BeTrue();
    }
}
