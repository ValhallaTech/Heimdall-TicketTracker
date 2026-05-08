using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.BLL.Tests.Authorization.OpenFga.TestSupport;
using Heimdall.Core.Auditing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenFga.Sdk.Client;

namespace Heimdall.BLL.Tests.Authorization.OpenFga;

/// <summary>
/// Unit tests for <see cref="OpenFgaTupleWriter"/>. Drives the writer through a
/// real <see cref="OpenFgaClient"/> wired to the <see cref="FakeHttpMessageHandler"/>
/// seam — same approach as <see cref="OpenFgaAuthorizationServiceTests"/> — because
/// the SDK methods are virtual-but-final and cannot be Moq'd.
/// </summary>
public class OpenFgaTupleWriterTests
{
    private const string StoreId = "01HZX0000000000000000000ST";
    private const string ModelId = "01HZX0000000000000000000MD";

    private static readonly TupleKey SampleWrite = new("user:u1", "viewer", "ticket:1");
    private static readonly TupleKey SampleDelete = new("user:u2", "viewer", "ticket:2");

    private static (OpenFgaTupleWriter Sut, FakeHttpMessageHandler Handler, Mock<IAuditEventWriter> Audit)
        CreateSut()
    {
        var handler = new FakeHttpMessageHandler
        {
            // Default: SDK Write succeeds with an empty 200 body.
            Responder = (_, _) => Task.FromResult(FakeHttpMessageHandler.Json("{}")),
        };
        var http = new HttpClient(handler);
        var cfg = new ClientConfiguration
        {
            ApiUrl = "http://fga.local",
            StoreId = StoreId,
            AuthorizationModelId = ModelId,
        };
        var client = new OpenFgaClient(cfg, http);
        var audit = new Mock<IAuditEventWriter>();
        var sut = new OpenFgaTupleWriter(
            client,
            audit.Object,
            NullLogger<OpenFgaTupleWriter>.Instance);
        return (sut, handler, audit);
    }

    // ─── Constructor guards ───────────────────────────────────────────────

    [Fact]
    public void Constructor_Should_Throw_When_ClientIsNull()
    {
        Action act = () => new OpenFgaTupleWriter(
            null!,
            Mock.Of<IAuditEventWriter>(),
            NullLogger<OpenFgaTupleWriter>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_Should_Throw_When_AuditWriterIsNull()
    {
        var client = new OpenFgaClient(
            new ClientConfiguration { ApiUrl = "http://x", StoreId = StoreId, AuthorizationModelId = ModelId },
            new HttpClient(new FakeHttpMessageHandler()));
        Action act = () => new OpenFgaTupleWriter(
            client,
            null!,
            NullLogger<OpenFgaTupleWriter>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_Should_Throw_When_LoggerIsNull()
    {
        var client = new OpenFgaClient(
            new ClientConfiguration { ApiUrl = "http://x", StoreId = StoreId, AuthorizationModelId = ModelId },
            new HttpClient(new FakeHttpMessageHandler()));
        Action act = () => new OpenFgaTupleWriter(client, Mock.Of<IAuditEventWriter>(), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ─── Null-arg guards on public methods ────────────────────────────────

    [Fact]
    public async Task WriteAsync_Should_Throw_When_WritesListIsNull()
    {
        var (sut, _, _) = CreateSut();

        Func<Task> act = () => sut.WriteAsync(null!, Array.Empty<TupleKey>(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task WriteAsync_Should_Throw_When_DeletesListIsNull()
    {
        var (sut, _, _) = CreateSut();

        Func<Task> act = () => sut.WriteAsync(new[] { SampleWrite }, null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task WriteAsync_Should_Throw_When_SingleTupleIsNull()
    {
        var (sut, _, _) = CreateSut();

        Func<Task> act = () => sut.WriteAsync((TupleKey)null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ─── Writes / deletes routing ─────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_Should_CallSdkOnceWithWritesOnly_When_DeletesEmpty()
    {
        var (sut, handler, audit) = CreateSut();

        await sut.WriteAsync(new[] { SampleWrite }, Array.Empty<TupleKey>(), CancellationToken.None);

        handler.CallCount.Should().Be(1);
        handler.Requests[0].Body.Should().Contain("\"writes\"");
        handler.Requests[0].Body.Should().NotContain("\"deletes\"");
        audit.Verify(
            a => a.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task WriteAsync_Should_CallSdkOnceWithDeletesOnly_When_WritesEmpty()
    {
        var (sut, handler, audit) = CreateSut();

        await sut.WriteAsync(Array.Empty<TupleKey>(), new[] { SampleDelete }, CancellationToken.None);

        handler.CallCount.Should().Be(1);
        handler.Requests[0].Body.Should().Contain("\"deletes\"");
        handler.Requests[0].Body.Should().NotContain("\"writes\"");
        audit.Verify(
            a => a.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task WriteAsync_Should_CallSdkOnceWithBothArrays_When_AtomicWritesAndDeletes()
    {
        var (sut, handler, _) = CreateSut();

        await sut.WriteAsync(
            new[] { SampleWrite },
            new[] { SampleDelete },
            CancellationToken.None);

        handler.CallCount.Should().Be(1);
        handler.Requests[0].Body.Should().Contain("\"writes\"");
        handler.Requests[0].Body.Should().Contain("\"deletes\"");
    }

    [Fact]
    public async Task WriteAsync_Should_NotCallSdk_When_BothListsEmpty()
    {
        var (sut, handler, audit) = CreateSut();

        await sut.WriteAsync(Array.Empty<TupleKey>(), Array.Empty<TupleKey>(), CancellationToken.None);

        handler.CallCount.Should().Be(0);
        audit.Verify(
            a => a.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task WriteAsync_Should_DelegateToBatchAsWriteOnly_When_SingleOverloadCalled()
    {
        var (sut, handler, _) = CreateSut();

        await sut.WriteAsync(SampleWrite, CancellationToken.None);

        handler.CallCount.Should().Be(1);
        handler.Requests[0].Body.Should().Contain("\"writes\"");
        handler.Requests[0].Body.Should().NotContain("\"deletes\"");
    }

    // ─── ReplaceAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task ReplaceAsync_Should_CallSdkOnceWithBothArrays_When_BothProvided()
    {
        var (sut, handler, _) = CreateSut();

        await sut.ReplaceAsync(SampleDelete, SampleWrite, CancellationToken.None);

        handler.CallCount.Should().Be(1);
        handler.Requests[0].Body.Should().Contain("\"writes\"");
        handler.Requests[0].Body.Should().Contain("\"deletes\"");
    }

    [Fact]
    public async Task ReplaceAsync_Should_CallSdkWithWritesOnly_When_DeleteIsNull()
    {
        var (sut, handler, _) = CreateSut();

        await sut.ReplaceAsync(null, SampleWrite, CancellationToken.None);

        handler.CallCount.Should().Be(1);
        handler.Requests[0].Body.Should().Contain("\"writes\"");
        handler.Requests[0].Body.Should().NotContain("\"deletes\"");
    }

    [Fact]
    public async Task ReplaceAsync_Should_CallSdkWithDeletesOnly_When_WriteIsNull()
    {
        var (sut, handler, _) = CreateSut();

        await sut.ReplaceAsync(SampleDelete, null, CancellationToken.None);

        handler.CallCount.Should().Be(1);
        handler.Requests[0].Body.Should().Contain("\"deletes\"");
        handler.Requests[0].Body.Should().NotContain("\"writes\"");
    }

    [Fact]
    public async Task ReplaceAsync_Should_NotCallSdk_When_BothNull()
    {
        var (sut, handler, audit) = CreateSut();

        await sut.ReplaceAsync(null, null, CancellationToken.None);

        handler.CallCount.Should().Be(0);
        audit.Verify(
            a => a.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ─── Failure contract ─────────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_Should_AuditAndSwallow_When_SdkThrowsGenericException()
    {
        var (sut, handler, audit) = CreateSut();
        handler.Responder = (_, _) =>
            Task.FromResult(FakeHttpMessageHandler.Json(
                "{\"code\":\"internal_error\",\"message\":\"boom\"}",
                HttpStatusCode.InternalServerError));

        Func<Task> act = () => sut.WriteAsync(
            new[] { SampleWrite },
            Array.Empty<TupleKey>(),
            CancellationToken.None);

        await act.Should().NotThrowAsync();
        audit.Verify(
            a => a.WriteAsync(
                It.Is<AuditEvent>(e => e.EventType == "openfga_tuple_write_failed"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task WriteAsync_Should_NotAudit_When_SdkReturnsDuplicateTupleError()
    {
        var (sut, handler, audit) = CreateSut();
        handler.Responder = (_, _) =>
            Task.FromResult(FakeHttpMessageHandler.Json(
                "{\"code\":\"write_failed_due_to_invalid_input\",\"message\":\"cannot write a tuple which already exists\"}",
                HttpStatusCode.BadRequest));

        Func<Task> act = () => sut.WriteAsync(
            new[] { SampleWrite },
            Array.Empty<TupleKey>(),
            CancellationToken.None);

        await act.Should().NotThrowAsync();
        audit.Verify(
            a => a.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task WriteAsync_Should_PropagateOperationCanceled_When_TokenCancelled()
    {
        var (sut, handler, audit) = CreateSut();
        handler.Responder = async (_, ct) =>
        {
            // Observe cancellation cooperatively so the OCE bubbles up rather than
            // being wrapped as a generic transport error.
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return FakeHttpMessageHandler.Json("{}");
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => sut.WriteAsync(
            new[] { SampleWrite },
            Array.Empty<TupleKey>(),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        audit.Verify(
            a => a.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
