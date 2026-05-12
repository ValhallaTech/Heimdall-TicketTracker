using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.BLL.Mapping;
using Heimdall.BLL.Services;
using Heimdall.Core.Auditing;
using Heimdall.Core.Dtos;
using Heimdall.BLL.Authorization;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Heimdall.BLL.Tests.Services;

/// <summary>
/// Integration-style unit tests for the OpenFGA tuple-write hooks in
/// <see cref="TicketService"/>. Uses a <see cref="CapturingTupleWriter"/>
/// test-double in place of a Moq mock so assertions on the emitted tuples
/// are expressed against a plain in-memory list rather than Moq callbacks.
/// </summary>
/// <remarks>
/// <para>
/// These tests complement the Moq-based tuple assertions in
/// <see cref="TicketServiceTests"/> by exercising the hooks through the real
/// <see cref="ITupleWriter"/> contract without mocking the interface itself.
/// The production repositories, cache, permissions, and connection factory are
/// all still mocked — this class does not require a running database or sidecar.
/// </para>
/// <para>
/// Marked <c>[Trait("Category", "Integration")]</c> per ValhallaTech conventions
/// for tests that exercise multiple components end-to-end without external
/// infrastructure.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class TicketServiceOpenFgaIntegrationTests
{
    // ─── Static seeds ─────────────────────────────────────────────────────────

    private static readonly Guid ActorId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly Guid ReporterId =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly Guid AssigneeId =
        Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private static readonly Guid ProjectId =
        Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private static readonly Guid TeamId =
        Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    // ─── Shared mocks ─────────────────────────────────────────────────────────

    private readonly Mock<ITicketRepository> _repository = new(MockBehavior.Strict);
    private readonly Mock<ICacheService> _cache = new(MockBehavior.Strict);
    private readonly Mock<IPermissionService> _permissions = new(MockBehavior.Loose);
    private readonly Mock<IDbConnectionFactory> _connectionFactory = new(MockBehavior.Loose);
    private readonly Mock<IAuditEventWriter> _auditWriter = new(MockBehavior.Loose);
    private readonly ITicketMapper _mapper = new TicketMapper();

    private TicketService BuildSut(CapturingTupleWriter capture) =>
        new(
            _repository.Object,
            _cache.Object,
            _mapper,
            _permissions.Object,
            _connectionFactory.Object,
            _auditWriter.Object,
            capture,
            NullLogger<TicketService>.Instance);

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private void WireConnection()
    {
        var transaction = new Mock<IDbTransaction>(MockBehavior.Loose);
        var connection = new Mock<IDbConnection>(MockBehavior.Loose);
        connection.Setup(c => c.BeginTransaction()).Returns(transaction.Object);
        _connectionFactory.Setup(f => f.CreateConnection()).Returns(connection.Object);

        _repository
            .Setup(r => r.UpdateAssigneeAsync(
                connection.Object, transaction.Object,
                It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _auditWriter
            .Setup(w => w.WriteAsync(
                connection.Object, transaction.Object,
                It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private static Ticket MakeTicket(int id, Guid? assigneeId = null) =>
        new()
        {
            Id = id,
            Title = "T",
            Description = "D",
            Status = TicketStatus.Open,
            Priority = TicketPriority.Medium,
            ProjectId = ProjectId,
            TeamId = TeamId,
            ReporterId = ReporterId,
            AssigneeId = assigneeId,
            DateCreated = DateTimeOffset.UtcNow,
            DateUpdated = DateTimeOffset.UtcNow,
        };

    // ─── CreateAsync — tuple hooks ─────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_Should_EmitParentProjectAndReporterTuples_When_NoAssignee()
    {
        // Arrange
        var dto = new TicketDto
        {
            Title = "T",
            Description = "D",
            ReporterId = ReporterId,
            ProjectId = ProjectId,
            TeamId = TeamId,
            AssigneeId = null,
        };
        _repository
            .Setup(r => r.CreateAsync(It.IsAny<Ticket>(), It.IsAny<CancellationToken>()))
            .Callback<Ticket, CancellationToken>((t, _) => t.Id = 42)
            .ReturnsAsync(42);
        _cache
            .Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var capture = new CapturingTupleWriter();
        var sut = BuildSut(capture);

        // Act
        await sut.CreateAsync(dto);

        // Assert
        capture.WriteAsyncCalls.Should().HaveCount(1, "a single WriteAsync call is expected for ticket creation");
        IReadOnlyList<TupleKey> written = capture.WriteAsyncCalls[0].Writes;
        written.Should().HaveCount(2, "parent_project + reporter; no assignee");
        written.Should().ContainSingle(t => t.Relation == TupleShapes.ParentProjectRelation);
        written.Should().ContainSingle(t => t.Relation == TupleShapes.ReporterRelation);
        written.Should().NotContain(t => t.Relation == TupleShapes.AssigneeRelation);

        // Verify exact TupleShapes format
        var parentProject = written.Single(t => t.Relation == TupleShapes.ParentProjectRelation);
        parentProject.Object.Should().Be($"{TupleShapes.TicketType}:42");
        parentProject.User.Should().Be($"{TupleShapes.ProjectType}:{ProjectId:D}");

        var reporter = written.Single(t => t.Relation == TupleShapes.ReporterRelation);
        reporter.Object.Should().Be($"{TupleShapes.TicketType}:42");
        reporter.User.Should().Be($"user:{ReporterId:D}");
    }

    [Fact]
    public async Task CreateAsync_Should_EmitThreeTuples_When_AssigneeIsSet()
    {
        // Arrange
        var dto = new TicketDto
        {
            Title = "T",
            Description = "D",
            ReporterId = ReporterId,
            ProjectId = ProjectId,
            TeamId = TeamId,
            AssigneeId = AssigneeId,
        };
        _repository
            .Setup(r => r.CreateAsync(It.IsAny<Ticket>(), It.IsAny<CancellationToken>()))
            .Callback<Ticket, CancellationToken>((t, _) => t.Id = 7)
            .ReturnsAsync(7);
        _cache
            .Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var capture = new CapturingTupleWriter();
        var sut = BuildSut(capture);

        // Act
        await sut.CreateAsync(dto);

        // Assert
        IReadOnlyList<TupleKey> written = capture.WriteAsyncCalls[0].Writes;
        written.Should().HaveCount(3, "parent_project + reporter + assignee");
        written.Should().ContainSingle(t => t.Relation == TupleShapes.ParentProjectRelation);
        written.Should().ContainSingle(t => t.Relation == TupleShapes.ReporterRelation);

        var assignee = written.Should().ContainSingle(t => t.Relation == TupleShapes.AssigneeRelation).Which;
        assignee.User.Should().Be($"user:{AssigneeId:D}");
        assignee.Object.Should().Be($"{TupleShapes.TicketType}:7");
    }

    // ─── AssignTicketAsync — ReplaceAsync hook ─────────────────────────────────

    [Fact]
    public async Task AssignTicketAsync_Should_ReplaceAssigneeTuple_When_PreviouslyAssigned()
    {
        // Arrange
        var previousAssignee = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var ticket = MakeTicket(id: 5, assigneeId: previousAssignee);

        _repository
            .Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ticket);
        _permissions
            .Setup(p => p.CanAssignTicketAsync(ActorId, ticket, AssigneeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _cache
            .Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        WireConnection();

        var capture = new CapturingTupleWriter();
        var sut = BuildSut(capture);

        // Act
        await sut.AssignTicketAsync(ActorId, 5, AssigneeId);

        // Assert
        capture.ReplaceAsyncCalls.Should().HaveCount(1, "one atomic replace per assignment");

        var replaceCall = capture.ReplaceAsyncCalls[0];
        replaceCall.Delete.Should().NotBeNull("previous assignee tuple must be deleted");
        replaceCall.Delete!.Relation.Should().Be(TupleShapes.AssigneeRelation);
        replaceCall.Delete.User.Should().Be($"user:{previousAssignee:D}");
        replaceCall.Delete.Object.Should().Be($"{TupleShapes.TicketType}:5");

        replaceCall.Write.Should().NotBeNull("new assignee tuple must be written");
        replaceCall.Write!.Relation.Should().Be(TupleShapes.AssigneeRelation);
        replaceCall.Write.User.Should().Be($"user:{AssigneeId:D}");
        replaceCall.Write.Object.Should().Be($"{TupleShapes.TicketType}:5");
    }

    [Fact]
    public async Task AssignTicketAsync_Should_WriteNewTuple_When_PreviouslyUnassigned()
    {
        // Arrange
        var ticket = MakeTicket(id: 9, assigneeId: null);

        _repository
            .Setup(r => r.GetByIdAsync(9, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ticket);
        _permissions
            .Setup(p => p.CanAssignTicketAsync(ActorId, ticket, AssigneeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _cache
            .Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        WireConnection();

        var capture = new CapturingTupleWriter();
        var sut = BuildSut(capture);

        // Act
        await sut.AssignTicketAsync(ActorId, 9, AssigneeId);

        // Assert
        var replaceCall = capture.ReplaceAsyncCalls.Should().ContainSingle().Which;
        replaceCall.Delete.Should().BeNull("no previous assignee → delete side is null");
        replaceCall.Write.Should().NotBeNull();
        replaceCall.Write!.User.Should().Be($"user:{AssigneeId:D}");
    }

    // ─── ClaimTicketAsync — self-assign shortcut ──────────────────────────────

    [Fact]
    public async Task ClaimTicketAsync_Should_SetTargetUserIdToActorId_In_ReplaceAsync()
    {
        // Arrange
        var ticket = MakeTicket(id: 3, assigneeId: null);

        _repository
            .Setup(r => r.GetByIdAsync(3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ticket);
        _permissions
            .Setup(p => p.CanAssignTicketAsync(ActorId, ticket, ActorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _cache
            .Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        WireConnection();

        var capture = new CapturingTupleWriter();
        var sut = BuildSut(capture);

        // Act
        await sut.ClaimTicketAsync(ActorId, 3);

        // Assert — ClaimTicketAsync delegates to AssignTicketAsync(actorId, ticketId, actorId).
        var replaceCall = capture.ReplaceAsyncCalls.Should().ContainSingle().Which;
        replaceCall.Write.Should().NotBeNull();
        replaceCall.Write!.User.Should().Be($"user:{ActorId:D}",
            because: "a claim sets targetUserId == actorId");
        replaceCall.Delete.Should().BeNull("ticket was previously unassigned");
    }

    // ─── CapturingTupleWriter ──────────────────────────────────────────────────

    /// <summary>
    /// In-process <see cref="ITupleWriter"/> test-double that records every
    /// <see cref="WriteAsync"/> and <see cref="ReplaceAsync"/> call so tests
    /// can assert on the exact tuples emitted by the system under test without
    /// relying on Moq <c>Callback</c> gymnastics.
    /// </summary>
    private sealed class CapturingTupleWriter : ITupleWriter
    {
        private readonly List<WriteAsyncCall> _writeAsyncCalls = new();
        private readonly List<ReplaceAsyncCall> _replaceAsyncCalls = new();

        /// <summary>Gets all recorded <see cref="WriteAsync"/> calls.</summary>
        public IReadOnlyList<WriteAsyncCall> WriteAsyncCalls => _writeAsyncCalls;

        /// <summary>Gets all recorded <see cref="ReplaceAsync"/> calls.</summary>
        public IReadOnlyList<ReplaceAsyncCall> ReplaceAsyncCalls => _replaceAsyncCalls;

        /// <inheritdoc />
        public Task WriteAsync(
            IReadOnlyList<TupleKey> writes,
            IReadOnlyList<TupleKey> deletes,
            CancellationToken cancellationToken = default)
        {
            _writeAsyncCalls.Add(new WriteAsyncCall(writes, deletes));
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task WriteAsync(TupleKey single, CancellationToken cancellationToken = default)
        {
            _writeAsyncCalls.Add(new WriteAsyncCall(
                new[] { single },
                Array.Empty<TupleKey>()));
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task ReplaceAsync(
            TupleKey? delete,
            TupleKey? write,
            CancellationToken cancellationToken = default)
        {
            _replaceAsyncCalls.Add(new ReplaceAsyncCall(delete, write));
            return Task.CompletedTask;
        }
    }

    /// <summary>Recorded parameters from a single <see cref="ITupleWriter.WriteAsync"/> call.</summary>
    private sealed record WriteAsyncCall(
        IReadOnlyList<TupleKey> Writes,
        IReadOnlyList<TupleKey> Deletes);

    /// <summary>Recorded parameters from a single <see cref="ITupleWriter.ReplaceAsync"/> call.</summary>
    private sealed record ReplaceAsyncCall(TupleKey? Delete, TupleKey? Write);
}
