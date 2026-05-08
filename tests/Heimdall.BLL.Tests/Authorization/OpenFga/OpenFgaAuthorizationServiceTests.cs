using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.BLL.Tests.Authorization.OpenFga.TestSupport;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenFga.Sdk.Client;

namespace Heimdall.BLL.Tests.Authorization.OpenFga;

/// <summary>
/// Unit tests for <see cref="OpenFgaAuthorizationService"/>. The OpenFGA SDK
/// client's transport methods are virtual-but-final and cannot be Moq'd, so
/// these tests fake responses at the <see cref="HttpMessageHandler"/> seam —
/// the same seam the SDK exposes via its public ctor.
/// </summary>
public class OpenFgaAuthorizationServiceTests
{
    private const string StoreId = "01HZX0000000000000000000ST";
    private const string ModelId = "01HZX0000000000000000000MD";

    private static readonly OpenFgaOptions DefaultOptions = new()
    {
        ApiUrl = "http://fga.local",
        StoreId = StoreId,
        AuthorizationModelId = ModelId,
        CacheTtl = TimeSpan.FromSeconds(2),
    };

    private static FgaCheckRequest SampleCheck(
        string user = "user:u1",
        string relation = "view",
        string @object = "ticket:1",
        FgaConsistency consistency = FgaConsistency.MinimizeLatency) =>
        new(user, relation, @object, consistency);

    private static (OpenFgaAuthorizationService Sut, FakeHttpMessageHandler Handler, IMemoryCache Cache)
        CreateSut(OpenFgaOptions? options = null)
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler);
        var cfg = new ClientConfiguration
        {
            ApiUrl = (options ?? DefaultOptions).ApiUrl,
            StoreId = (options ?? DefaultOptions).StoreId,
            AuthorizationModelId = (options ?? DefaultOptions).AuthorizationModelId,
        };
        var client = new OpenFgaClient(cfg, http);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = new OpenFgaAuthorizationService(
            client,
            cache,
            Options.Create(options ?? DefaultOptions),
            NullLogger<OpenFgaAuthorizationService>.Instance);
        return (sut, handler, cache);
    }

    // ─── Constructor guards ───────────────────────────────────────────────

    [Fact]
    public void Constructor_Should_Throw_When_ClientIsNull()
    {
        Action act = () => new OpenFgaAuthorizationService(
            null!,
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(DefaultOptions),
            NullLogger<OpenFgaAuthorizationService>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_Should_Throw_When_CacheIsNull()
    {
        var client = new OpenFgaClient(
            new ClientConfiguration { ApiUrl = "http://x", StoreId = StoreId, AuthorizationModelId = ModelId },
            new HttpClient(new FakeHttpMessageHandler()));
        Action act = () => new OpenFgaAuthorizationService(
            client,
            null!,
            Options.Create(DefaultOptions),
            NullLogger<OpenFgaAuthorizationService>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_Should_Throw_When_OptionsIsNull()
    {
        var client = new OpenFgaClient(
            new ClientConfiguration { ApiUrl = "http://x", StoreId = StoreId, AuthorizationModelId = ModelId },
            new HttpClient(new FakeHttpMessageHandler()));
        Action act = () => new OpenFgaAuthorizationService(
            client,
            new MemoryCache(new MemoryCacheOptions()),
            null!,
            NullLogger<OpenFgaAuthorizationService>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_Should_Throw_When_LoggerIsNull()
    {
        var client = new OpenFgaClient(
            new ClientConfiguration { ApiUrl = "http://x", StoreId = StoreId, AuthorizationModelId = ModelId },
            new HttpClient(new FakeHttpMessageHandler()));
        Action act = () => new OpenFgaAuthorizationService(
            client,
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(DefaultOptions),
            null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ─── CheckAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_Should_ReturnTrue_When_SidecarAllows()
    {
        var (sut, handler, _) = CreateSut();
        handler.Responder = (_, _) => Task.FromResult(FakeHttpMessageHandler.Json("{\"allowed\":true}"));

        var result = await sut.CheckAsync(SampleCheck(), CancellationToken.None);

        result.Should().BeTrue();
        handler.CallCount.Should().Be(1);
        handler.Requests[0].Uri.AbsolutePath.Should().EndWith("/check");
    }

    [Fact]
    public async Task CheckAsync_Should_ReturnFalse_When_SidecarDenies()
    {
        var (sut, handler, _) = CreateSut();
        handler.Responder = (_, _) => Task.FromResult(FakeHttpMessageHandler.Json("{\"allowed\":false}"));

        var result = await sut.CheckAsync(SampleCheck(), CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAsync_Should_HitCacheOnSecondCall_When_RequestIsIdentical()
    {
        var (sut, handler, _) = CreateSut();
        handler.Responder = (_, _) => Task.FromResult(FakeHttpMessageHandler.Json("{\"allowed\":true}"));

        bool first = await sut.CheckAsync(SampleCheck(), CancellationToken.None);
        bool second = await sut.CheckAsync(SampleCheck(), CancellationToken.None);

        first.Should().BeTrue();
        second.Should().BeTrue();
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task CheckAsync_Should_CacheNegativeResult_When_SidecarDeniesTwice()
    {
        var (sut, handler, _) = CreateSut();
        handler.Responder = (_, _) => Task.FromResult(FakeHttpMessageHandler.Json("{\"allowed\":false}"));

        bool first = await sut.CheckAsync(SampleCheck(), CancellationToken.None);
        bool second = await sut.CheckAsync(SampleCheck(), CancellationToken.None);

        first.Should().BeFalse();
        second.Should().BeFalse();
        handler.CallCount.Should().Be(1);
    }

    [Theory]
    [InlineData("user:u1", "user:u2", "view", "view", "ticket:1", "ticket:1", FgaConsistency.MinimizeLatency, FgaConsistency.MinimizeLatency)]
    [InlineData("user:u1", "user:u1", "view", "edit", "ticket:1", "ticket:1", FgaConsistency.MinimizeLatency, FgaConsistency.MinimizeLatency)]
    [InlineData("user:u1", "user:u1", "view", "view", "ticket:1", "ticket:2", FgaConsistency.MinimizeLatency, FgaConsistency.MinimizeLatency)]
    [InlineData("user:u1", "user:u1", "view", "view", "ticket:1", "ticket:1", FgaConsistency.MinimizeLatency, FgaConsistency.HigherConsistency)]
    public async Task CheckAsync_Should_MissCache_When_AnyKeyComponentDiffers(
        string user1, string user2,
        string relation1, string relation2,
        string object1, string object2,
        FgaConsistency consistency1, FgaConsistency consistency2)
    {
        var (sut, handler, _) = CreateSut();
        handler.Responder = (_, _) => Task.FromResult(FakeHttpMessageHandler.Json("{\"allowed\":true}"));

        await sut.CheckAsync(new FgaCheckRequest(user1, relation1, object1, consistency1), CancellationToken.None);
        await sut.CheckAsync(new FgaCheckRequest(user2, relation2, object2, consistency2), CancellationToken.None);

        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task CheckAsync_Should_DenyClosed_When_SidecarThrows()
    {
        var (sut, handler, _) = CreateSut();
        handler.Responder = (_, _) => throw new HttpRequestException("sidecar down");

        var result = await sut.CheckAsync(SampleCheck(), CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAsync_Should_DenyClosed_When_SidecarReturns500()
    {
        var (sut, handler, _) = CreateSut();
        handler.Responder = (_, _) => Task.FromResult(
            FakeHttpMessageHandler.Json("{\"code\":\"internal_error\"}", HttpStatusCode.InternalServerError));

        var result = await sut.CheckAsync(SampleCheck(), CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAsync_Should_PropagateOperationCanceled_When_TokenCancelled()
    {
        var (sut, _, _) = CreateSut();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => sut.CheckAsync(SampleCheck(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CheckAsync_Should_Throw_When_RequestIsNull()
    {
        var (sut, _, _) = CreateSut();

        Func<Task> act = () => sut.CheckAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ─── BatchCheckAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task BatchCheckAsync_Should_ReturnEmpty_When_RequestsIsEmpty()
    {
        var (sut, handler, _) = CreateSut();

        var results = await sut.BatchCheckAsync(Array.Empty<FgaCheckRequest>(), CancellationToken.None);

        results.Should().BeEmpty();
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task BatchCheckAsync_Should_ReturnResultsInInputOrder_When_SidecarReturnsOutOfOrder()
    {
        var (sut, handler, _) = CreateSut();
        // The SDK keys the result by correlation_id; the production service uses
        // the original index ("0", "1", ...) so out-of-order responses still hit
        // the right slot.
        handler.Responder = (_, _) => Task.FromResult(FakeHttpMessageHandler.Json(
            "{\"result\":{\"1\":{\"allowed\":true},\"0\":{\"allowed\":false}}}"));

        var results = await sut.BatchCheckAsync(new[]
        {
            SampleCheck(@object: "ticket:10"),
            SampleCheck(@object: "ticket:11"),
        }, CancellationToken.None);

        results.Should().HaveCount(2);
        results[0].Should().BeFalse();
        results[1].Should().BeTrue();
    }

    [Fact]
    public async Task BatchCheckAsync_Should_SkipSdkCall_When_AllEntriesAreCached()
    {
        var (sut, handler, _) = CreateSut();
        handler.Responder = (_, _) => Task.FromResult(FakeHttpMessageHandler.Json("{\"allowed\":true}"));

        await sut.CheckAsync(SampleCheck(@object: "ticket:1"), CancellationToken.None);
        await sut.CheckAsync(SampleCheck(@object: "ticket:2"), CancellationToken.None);
        int afterPrime = handler.CallCount;

        var results = await sut.BatchCheckAsync(new[]
        {
            SampleCheck(@object: "ticket:1"),
            SampleCheck(@object: "ticket:2"),
        }, CancellationToken.None);

        results.Should().Equal(true, true);
        handler.CallCount.Should().Be(afterPrime);
    }

    [Fact]
    public async Task BatchCheckAsync_Should_DenyClosed_When_SidecarThrows()
    {
        var (sut, handler, _) = CreateSut();
        handler.Responder = (_, _) => throw new HttpRequestException("sidecar down");

        var results = await sut.BatchCheckAsync(new[]
        {
            SampleCheck(@object: "ticket:1"),
            SampleCheck(@object: "ticket:2"),
        }, CancellationToken.None);

        results.Should().Equal(false, false);
    }

    [Fact]
    public async Task BatchCheckAsync_Should_PropagateOperationCanceled_When_TokenCancelled()
    {
        var (sut, _, _) = CreateSut();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => sut.BatchCheckAsync(new[] { SampleCheck() }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task BatchCheckAsync_Should_Throw_When_RequestsIsNull()
    {
        var (sut, _, _) = CreateSut();

        Func<Task> act = () => sut.BatchCheckAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ─── ListObjectsAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task ListObjectsAsync_Should_ReturnObjects_When_SidecarSucceeds()
    {
        var (sut, handler, _) = CreateSut();
        handler.Responder = (_, _) => Task.FromResult(FakeHttpMessageHandler.Json(
            "{\"objects\":[\"ticket:1\",\"ticket:2\"]}"));

        var result = await sut.ListObjectsAsync(
            new FgaListObjectsRequest("user:u1", "view", "ticket"),
            CancellationToken.None);

        result.Should().Equal("ticket:1", "ticket:2");
    }

    [Fact]
    public async Task ListObjectsAsync_Should_ReturnEmpty_When_SidecarThrows()
    {
        var (sut, handler, _) = CreateSut();
        handler.Responder = (_, _) => throw new HttpRequestException("sidecar down");

        var result = await sut.ListObjectsAsync(
            new FgaListObjectsRequest("user:u1", "view", "ticket"),
            CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListObjectsAsync_Should_PropagateOperationCanceled_When_TokenCancelled()
    {
        var (sut, _, _) = CreateSut();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => sut.ListObjectsAsync(
            new FgaListObjectsRequest("user:u1", "view", "ticket"),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ListObjectsAsync_Should_Throw_When_RequestIsNull()
    {
        var (sut, _, _) = CreateSut();

        Func<Task> act = () => sut.ListObjectsAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
