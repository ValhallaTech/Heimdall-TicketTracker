using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.BLL.Tests.Authorization.OpenFga.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Heimdall.BLL.Tests.Authorization.OpenFga;

/// <summary>
/// Unit tests for the deny-closed no-op fall-backs registered when no OpenFGA
/// sidecar is configured. The classes themselves are <c>internal sealed</c>, so
/// these tests resolve them via <see cref="OpenFgaServiceCollectionExtensions"/>
/// and exercise the public service contracts.
/// </summary>
[Collection(EnvironmentVariableSerialCollection.Name)]
public class NoOpOpenFgaServicesTests
{
    private static EnvironmentVariableScope ClearOpenFgaEnv() =>
        new(new Dictionary<string, string?>
        {
            [OpenFgaServiceCollectionExtensions.ApiUrlEnvVar] = null,
            [OpenFgaServiceCollectionExtensions.StoreIdEnvVar] = null,
            [OpenFgaServiceCollectionExtensions.AuthorizationModelIdEnvVar] = null,
            [OpenFgaServiceCollectionExtensions.PresharedKeyEnvVar] = null,
        });

    private static ServiceProvider BuildEmptyProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        services.AddHeimdallOpenFga(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task NoOpAuthorizationService_CheckAsync_Should_ReturnFalse()
    {
        using var envScope = ClearOpenFgaEnv();
        using var sp = BuildEmptyProvider();
        var sut = sp.GetRequiredService<IOpenFgaAuthorizationService>();
        sut.GetType().Name.Should().Be("NoOpOpenFgaAuthorizationService");

        bool result = await sut.CheckAsync(
            new FgaCheckRequest("user:u1", "view", "ticket:1", FgaConsistency.MinimizeLatency),
            CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task NoOpAuthorizationService_BatchCheckAsync_Should_ReturnAllFalseMatchingInputLength()
    {
        using var envScope = ClearOpenFgaEnv();
        using var sp = BuildEmptyProvider();
        var sut = sp.GetRequiredService<IOpenFgaAuthorizationService>();

        IReadOnlyList<bool> results = await sut.BatchCheckAsync(
            new[]
            {
                new FgaCheckRequest("user:u1", "view", "ticket:1", FgaConsistency.MinimizeLatency),
                new FgaCheckRequest("user:u2", "view", "ticket:2", FgaConsistency.MinimizeLatency),
                new FgaCheckRequest("user:u3", "edit", "ticket:3", FgaConsistency.MinimizeLatency),
            },
            CancellationToken.None);

        results.Should().HaveCount(3);
        results.Should().OnlyContain(b => b == false);
    }

    [Fact]
    public async Task NoOpAuthorizationService_ListObjectsAsync_Should_ReturnEmpty()
    {
        using var envScope = ClearOpenFgaEnv();
        using var sp = BuildEmptyProvider();
        var sut = sp.GetRequiredService<IOpenFgaAuthorizationService>();

        IReadOnlyList<string> result = await sut.ListObjectsAsync(
            new FgaListObjectsRequest("user:u1", "view", "ticket"),
            CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task NoOpTupleWriter_WriteAsyncBatch_Should_BeNoOp()
    {
        using var envScope = ClearOpenFgaEnv();
        using var sp = BuildEmptyProvider();
        var sut = sp.GetRequiredService<ITupleWriter>();
        sut.GetType().Name.Should().Be("NoOpTupleWriter");

        Func<Task> act = () => sut.WriteAsync(
            new[] { new TupleKey("user:u1", "viewer", "ticket:1") },
            new[] { new TupleKey("user:u2", "viewer", "ticket:2") },
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NoOpTupleWriter_WriteAsyncSingle_Should_BeNoOp()
    {
        using var envScope = ClearOpenFgaEnv();
        using var sp = BuildEmptyProvider();
        var sut = sp.GetRequiredService<ITupleWriter>();

        Func<Task> act = () => sut.WriteAsync(
            new TupleKey("user:u1", "viewer", "ticket:1"),
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NoOpTupleWriter_ReplaceAsync_Should_BeNoOp()
    {
        using var envScope = ClearOpenFgaEnv();
        using var sp = BuildEmptyProvider();
        var sut = sp.GetRequiredService<ITupleWriter>();

        Func<Task> act = () => sut.ReplaceAsync(
            new TupleKey("user:u1", "assignee", "ticket:1"),
            new TupleKey("user:u2", "assignee", "ticket:1"),
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
