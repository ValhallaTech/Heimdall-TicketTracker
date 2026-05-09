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

    // ─── ListUsersAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task ListUsersAsync_Should_ReturnBareUserIds_When_SidecarReturnsObjectSubjects()
    {
        var (sut, handler, _) = CreateSut();
        // ListUsers response shape per OpenFGA: { users: [ { object: { type, id } } ] }
        // The adapter strips the type prefix and surfaces only `user:` subjects.
        handler.Responder = (_, _) => Task.FromResult(FakeHttpMessageHandler.Json(
            "{\"users\":[" +
            "{\"object\":{\"type\":\"user\",\"id\":\"alice\"}}," +
            "{\"object\":{\"type\":\"user\",\"id\":\"bob\"}}" +
            "]}"));

        var result = await sut.ListUsersAsync(
            new FgaListUsersRequest("ticket", "42", "view"),
            CancellationToken.None);

        result.Should().Equal("alice", "bob");
        handler.CallCount.Should().Be(1);
        handler.Requests[0].Uri.AbsolutePath.Should().EndWith("/list-users");
    }

    [Fact]
    public async Task ListUsersAsync_Should_FilterOutNonUserAndUsersetSubjects()
    {
        var (sut, handler, _) = CreateSut();
        // Defensively skip wildcard / userset / non-user subjects even though
        // the model bans them — the production adapter promises bare user ids
        // so callers can resolve via IUserLookup without a per-row parse.
        handler.Responder = (_, _) => Task.FromResult(FakeHttpMessageHandler.Json(
            "{\"users\":[" +
            "{\"object\":{\"type\":\"user\",\"id\":\"alice\"}}," +
            "{\"object\":{\"type\":\"team\",\"id\":\"t-1\"}}," +
            "{\"userset\":{\"type\":\"team\",\"id\":\"t-1\",\"relation\":\"member\"}}," +
            "{\"wildcard\":{\"type\":\"user\"}}," +
            "{\"object\":{\"type\":\"user\",\"id\":\"\"}}," +
            "{\"object\":{\"type\":\"user\",\"id\":\"bob\"}}" +
            "]}"));

        var result = await sut.ListUsersAsync(
            new FgaListUsersRequest("ticket", "42", "view"),
            CancellationToken.None);

        result.Should().Equal("alice", "bob");
    }

    [Fact]
    public async Task ListUsersAsync_Should_ReturnEmpty_When_SidecarReturnsNoUsers()
    {
        var (sut, handler, _) = CreateSut();
        handler.Responder = (_, _) => Task.FromResult(FakeHttpMessageHandler.Json("{\"users\":[]}"));

        var result = await sut.ListUsersAsync(
            new FgaListUsersRequest("ticket", "42", "view"),
            CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListUsersAsync_Should_ReturnEmpty_When_SidecarThrows()
    {
        var (sut, handler, _) = CreateSut();
        handler.Responder = (_, _) => throw new HttpRequestException("sidecar down");

        var result = await sut.ListUsersAsync(
            new FgaListUsersRequest("ticket", "42", "view"),
            CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListUsersAsync_Should_ReturnEmpty_When_SidecarReturns500()
    {
        var (sut, handler, _) = CreateSut();
        handler.Responder = (_, _) => Task.FromResult(
            FakeHttpMessageHandler.Json("{\"code\":\"internal_error\"}", HttpStatusCode.InternalServerError));

        var result = await sut.ListUsersAsync(
            new FgaListUsersRequest("ticket", "42", "view"),
            CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListUsersAsync_Should_PropagateOperationCanceled_When_TokenCancelled()
    {
        var (sut, _, _) = CreateSut();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => sut.ListUsersAsync(
            new FgaListUsersRequest("ticket", "42", "view"),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ListUsersAsync_Should_Throw_When_RequestIsNull()
    {
        var (sut, _, _) = CreateSut();

        Func<Task> act = () => sut.ListUsersAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ─── ExpandAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExpandAsync_Should_ReturnTree_When_SidecarReturnsLeafWithUsers()
    {
        var (sut, handler, _) = CreateSut();
        handler.Responder = (_, _) => Task.FromResult(FakeHttpMessageHandler.Json(
            "{\"tree\":{\"root\":{\"name\":\"ticket:42#view\"," +
            "\"leaf\":{\"users\":{\"users\":[\"user:alice\",\"user:bob\"]}}}}}"));

        var result = await sut.ExpandAsync(
            new FgaExpandRequest("ticket", "42", "view"),
            CancellationToken.None);

        result.Root.Should().NotBeNull();
        result.Root!.Name.Should().Be("ticket:42#view");
        result.Root.Leaf.Should().NotBeNull();
        result.Root.Leaf!.Users.Should().Equal("user:alice", "user:bob");
        result.Root.Leaf.ComputedUserset.Should().BeNull();
        result.Root.Leaf.TupleToUserset.Should().BeNull();
        handler.Requests[0].Uri.AbsolutePath.Should().EndWith("/expand");
    }

    [Fact]
    public async Task ExpandAsync_Should_ProjectComputedAndTupleToUsersetLeaves()
    {
        var (sut, handler, _) = CreateSut();
        // Mix of computed-userset and tuple-to-userset leaves nested under union /
        // intersection / difference branches — the production walker must handle
        // every userset operator the OpenFGA DSL emits.
        handler.Responder = (_, _) => Task.FromResult(FakeHttpMessageHandler.Json(
            "{\"tree\":{\"root\":{\"name\":\"ticket:42#view\"," +
            "\"union\":{\"nodes\":[" +
                "{\"name\":\"a\",\"leaf\":{\"computed\":{\"userset\":\"project:p#admin\"}}}," +
                "{\"name\":\"b\",\"leaf\":{\"tupleToUserset\":{\"tupleset\":\"parent_project\",\"computed\":[{\"userset\":\"#viewer\"}]}}}," +
                "{\"name\":\"c\",\"intersection\":{\"nodes\":[" +
                    "{\"name\":\"c1\",\"leaf\":{\"users\":{\"users\":[\"user:x\"]}}}" +
                "]}}," +
                "{\"name\":\"d\",\"difference\":{" +
                    "\"base\":{\"name\":\"d1\",\"leaf\":{\"users\":{\"users\":[\"user:y\"]}}}," +
                    "\"subtract\":{\"name\":\"d2\",\"leaf\":{\"users\":{\"users\":[\"user:z\"]}}}}}" +
            "]}}}}"));

        var result = await sut.ExpandAsync(
            new FgaExpandRequest("ticket", "42", "view"),
            CancellationToken.None);

        result.Root.Should().NotBeNull();
        result.Root!.Union.Should().NotBeNull();
        result.Root.Union!.Should().HaveCount(4);

        // a — computed-userset leaf.
        result.Root.Union[0].Leaf!.ComputedUserset.Should().Be("project:p#admin");

        // b — tuple-to-userset leaf.
        result.Root.Union[1].Leaf!.TupleToUserset.Should().NotBeNull();
        result.Root.Union[1].Leaf!.TupleToUserset!.Tupleset.Should().Be("parent_project");
        result.Root.Union[1].Leaf!.TupleToUserset!.ComputedUsersets.Should().Equal("#viewer");

        // c — intersection branch.
        result.Root.Union[2].Intersection.Should().NotBeNull();
        result.Root.Union[2].Intersection!.Should().HaveCount(1);
        result.Root.Union[2].Intersection![0].Leaf!.Users.Should().Equal("user:x");

        // d — difference branch.
        result.Root.Union[3].Difference.Should().NotBeNull();
        result.Root.Union[3].Difference!.Base.Leaf!.Users.Should().Equal("user:y");
        result.Root.Union[3].Difference!.Subtract.Leaf!.Users.Should().Equal("user:z");
    }

    [Fact]
    public async Task ExpandAsync_Should_ReturnEmptyResult_When_SidecarReturnsNullTree()
    {
        var (sut, handler, _) = CreateSut();
        handler.Responder = (_, _) => Task.FromResult(FakeHttpMessageHandler.Json("{}"));

        var result = await sut.ExpandAsync(
            new FgaExpandRequest("ticket", "42", "view"),
            CancellationToken.None);

        result.Root.Should().BeNull();
    }

    [Fact]
    public async Task ExpandAsync_Should_ReturnEmptyResult_When_SidecarThrows()
    {
        var (sut, handler, _) = CreateSut();
        handler.Responder = (_, _) => throw new HttpRequestException("sidecar down");

        var result = await sut.ExpandAsync(
            new FgaExpandRequest("ticket", "42", "view"),
            CancellationToken.None);

        result.Root.Should().BeNull();
    }

    [Fact]
    public async Task ExpandAsync_Should_ReturnEmptyResult_When_SidecarReturns500()
    {
        var (sut, handler, _) = CreateSut();
        handler.Responder = (_, _) => Task.FromResult(
            FakeHttpMessageHandler.Json("{\"code\":\"internal_error\"}", HttpStatusCode.InternalServerError));

        var result = await sut.ExpandAsync(
            new FgaExpandRequest("ticket", "42", "view"),
            CancellationToken.None);

        result.Root.Should().BeNull();
    }

    [Fact]
    public async Task ExpandAsync_Should_PropagateOperationCanceled_When_TokenCancelled()
    {
        var (sut, _, _) = CreateSut();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => sut.ExpandAsync(
            new FgaExpandRequest("ticket", "42", "view"),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExpandAsync_Should_Throw_When_RequestIsNull()
    {
        var (sut, _, _) = CreateSut();

        Func<Task> act = () => sut.ExpandAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
