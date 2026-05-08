using System.Data;
using System.Text.Json;
using FluentAssertions;
using Heimdall.BLL.Authorization;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.BLL.Mapping;
using Heimdall.BLL.Services;
using Heimdall.Core.Auditing;
using Heimdall.Core.Caching;
using Heimdall.Core.Dtos;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Heimdall.Core.Models.Pagination;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Heimdall.BLL.Tests.Services;

public class TicketServiceTests
{
    private readonly Mock<ITicketRepository> _repository = new(MockBehavior.Strict);
    private readonly Mock<ICacheService> _cache = new(MockBehavior.Strict);
    private readonly Mock<IPermissionService> _permissions = new(MockBehavior.Loose);
    private readonly Mock<IDbConnectionFactory> _connectionFactory = new(MockBehavior.Loose);
    private readonly Mock<IAuditEventWriter> _auditWriter = new(MockBehavior.Loose);
    private readonly Mock<ITupleWriter> _tupleWriter = new(MockBehavior.Loose);
    private readonly ITicketMapper _mapper = new TicketMapper();

    private TicketService CreateSut() =>
        new(
            _repository.Object,
            _cache.Object,
            _mapper,
            _permissions.Object,
            _connectionFactory.Object,
            _auditWriter.Object,
            _tupleWriter.Object,
            NullLogger<TicketService>.Instance);

    private static readonly Guid SeedReporterId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private static Ticket SampleTicket(int id = 1) =>
        new()
        {
            Id = id,
            Title = $"Title {id}",
            Description = $"Desc {id}",
            Status = TicketStatus.Open,
            Priority = TicketPriority.Medium,
            ReporterId = SeedReporterId,
            AssigneeId = null,
            DateCreated = DateTimeOffset.UtcNow,
            DateUpdated = DateTimeOffset.UtcNow,
        };

    [Fact]
    public void Should_Throw_When_RepositoryIsNull()
    {
        Action act = () =>
            new TicketService(
                null!,
                _cache.Object,
                _mapper,
                _permissions.Object,
                _connectionFactory.Object,
                _auditWriter.Object,
                _tupleWriter.Object,
            NullLogger<TicketService>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_CacheIsNull()
    {
        Action act = () =>
            new TicketService(
                _repository.Object,
                null!,
                _mapper,
                _permissions.Object,
                _connectionFactory.Object,
                _auditWriter.Object,
                _tupleWriter.Object,
            NullLogger<TicketService>.Instance
            );
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_MapperIsNull()
    {
        Action act = () =>
            new TicketService(
                _repository.Object,
                _cache.Object,
                null!,
                _permissions.Object,
                _connectionFactory.Object,
                _auditWriter.Object,
                _tupleWriter.Object,
            NullLogger<TicketService>.Instance
            );
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_LoggerIsNull()
    {
        Action act = () => new TicketService(
            _repository.Object,
            _cache.Object,
            _mapper,
            _permissions.Object,
            _connectionFactory.Object,
            _auditWriter.Object,
            _tupleWriter.Object,
            null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_PermissionsIsNull()
    {
        Action act = () => new TicketService(
            _repository.Object,
            _cache.Object,
            _mapper,
            null!,
            _connectionFactory.Object,
            _auditWriter.Object,
            _tupleWriter.Object,
            NullLogger<TicketService>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_ReturnRepositoryRowsAndPopulateCache_When_GetAllAsyncMisses()
    {
        var tickets = new[] { SampleTicket(1), SampleTicket(2) };
        var cache = new RecordingCacheService();
        _repository.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(tickets);

        var sut = new TicketService(
            _repository.Object,
            cache,
            _mapper,
            _permissions.Object,
            _connectionFactory.Object,
            _auditWriter.Object,
            _tupleWriter.Object,
            NullLogger<TicketService>.Instance
        );

        var result = await sut.GetAllAsync();

        result.Should().HaveCount(2);
        result.Select(d => d.Id).Should().BeEquivalentTo(new[] { 1, 2 });
        cache.GetCalls.Should().Be(1);
        cache.SetCalls.Should().Be(1);
        cache.LastSetKey.Should().Be(CacheKeys.TicketList);
        cache.LastSetTtl.Should().Be(TimeSpan.FromMinutes(5));
        _repository.Verify(r => r.GetAllAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Should_ReturnCachedRows_When_GetAllAsyncHits()
    {
        // Round-trip through the in-memory cache so the service's own private wrapper type
        // is what's stored — exercising the "cache hit" early-return branch.
        var cache = new RecordingCacheService();
        _repository
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { SampleTicket(99) });

        var sut = new TicketService(
            _repository.Object,
            cache,
            _mapper,
            _permissions.Object,
            _connectionFactory.Object,
            _auditWriter.Object,
            _tupleWriter.Object,
            NullLogger<TicketService>.Instance
        );

        // First call: miss + set
        await sut.GetAllAsync();
        // Second call: hit (no repository call)
        var result = await sut.GetAllAsync();

        result.Should().HaveCount(1);
        result[0].Id.Should().Be(99);
        _repository.Verify(r => r.GetAllAsync(It.IsAny<CancellationToken>()), Times.Once);
        cache.GetCalls.Should().Be(2);
        cache.SetCalls.Should().Be(1);
    }

    /// <summary>
    /// Minimal in-memory <see cref="ICacheService"/> stub that round-trips reference values
    /// keyed by string. Used to exercise the read-through cache flow against the service's
    /// own internal wrapper type without reflection or strict-mock generic gymnastics.
    /// </summary>
    private sealed class RecordingCacheService : ICacheService
    {
        private readonly Dictionary<string, object?> _store = new(StringComparer.Ordinal);

        public int GetCalls { get; private set; }

        public int SetCalls { get; private set; }

        public string? LastSetKey { get; private set; }

        public TimeSpan? LastSetTtl { get; private set; }

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
            where T : class
        {
            GetCalls++;
            return Task.FromResult(_store.TryGetValue(key, out var v) ? (T?)v : null);
        }

        public Task SetAsync<T>(
            string key,
            T value,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default
        )
            where T : class
        {
            SetCalls++;
            LastSetKey = key;
            LastSetTtl = ttl;
            _store[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _store.Remove(key);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Should_ReturnPagedResult_When_GetPagedAsyncCalled()
    {
        var query = new PagedQuery(2, 10, "term", "Title", SortDirection.Ascending);
        var rows = new[] { SampleTicket(1), SampleTicket(2) };
        _repository
            .Setup(r => r.GetPagedAsync(It.IsAny<PagedQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((rows, 42));

        var sut = CreateSut();

        var result = await sut.GetPagedAsync(query);

        result.TotalCount.Should().Be(42);
        result.Page.Should().Be(2);
        result.PageSize.Should().Be(10);
        result.Items.Should().HaveCount(2);
        result.TotalPages.Should().Be(5);
    }

    [Fact]
    public async Task Should_Throw_When_GetPagedAsyncQueryIsNull()
    {
        var sut = CreateSut();
        Func<Task> act = () => sut.GetPagedAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_ReturnNull_When_GetByIdAsyncMisses()
    {
        _repository
            .Setup(r => r.GetByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Ticket?)null);
        var sut = CreateSut();

        var result = await sut.GetByIdAsync(7);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Should_ReturnDto_When_GetByIdAsyncHits()
    {
        var ticket = SampleTicket(7);
        _repository
            .Setup(r => r.GetByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ticket);
        var sut = CreateSut();

        var result = await sut.GetByIdAsync(7);

        result.Should().NotBeNull();
        result!.Id.Should().Be(7);
    }

    [Fact]
    public async Task Should_CreateAndInvalidateCache_When_CreateAsyncCalled()
    {
        var dto = new TicketDto
        {
            Title = "T",
            Description = "D",
            ReporterId = SeedReporterId,
        };
        Ticket? saved = null;
        _repository
            .Setup(r => r.CreateAsync(It.IsAny<Ticket>(), It.IsAny<CancellationToken>()))
            .Callback<Ticket, CancellationToken>(
                (t, _) =>
                {
                    saved = t;
                    t.Id = 11;
                }
            )
            .ReturnsAsync(11);
        _cache
            .Setup(c => c.RemoveAsync(CacheKeys.TicketList, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();

        var result = await sut.CreateAsync(dto);

        result.Title.Should().Be("T");
        saved.Should().NotBeNull();
        saved!.DateCreated.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        saved!.DateUpdated.Should().Be(saved.DateCreated);
        _cache.Verify(
            c => c.RemoveAsync(CacheKeys.TicketList, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task Should_Throw_When_CreateAsyncDtoIsNull()
    {
        var sut = CreateSut();
        Func<Task> act = () => sut.CreateAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_InvalidateCache_When_UpdateAsyncSucceeds()
    {
        var dto = new TicketDto
        {
            Id = 1,
            Title = "T",
            Description = "D",
            ReporterId = SeedReporterId,
        };
        _repository
            .Setup(r => r.UpdateAsync(It.IsAny<Ticket>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _cache
            .Setup(c => c.RemoveAsync(CacheKeys.TicketList, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        var result = await sut.UpdateAsync(dto);

        result.Should().BeTrue();
        _cache.Verify(
            c => c.RemoveAsync(CacheKeys.TicketList, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task Should_NotInvalidateCache_When_UpdateAsyncReturnsFalse()
    {
        var dto = new TicketDto
        {
            Id = 1,
            Title = "T",
            Description = "D",
            ReporterId = SeedReporterId,
        };
        _repository
            .Setup(r => r.UpdateAsync(It.IsAny<Ticket>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var sut = CreateSut();
        var result = await sut.UpdateAsync(dto);

        result.Should().BeFalse();
        _cache.Verify(
            c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task Should_Throw_When_UpdateAsyncDtoIsNull()
    {
        var sut = CreateSut();
        Func<Task> act = () => sut.UpdateAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_InvalidateCache_When_DeleteAsyncSucceeds()
    {
        _repository.Setup(r => r.DeleteAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _cache
            .Setup(c => c.RemoveAsync(CacheKeys.TicketList, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        var result = await sut.DeleteAsync(1);

        result.Should().BeTrue();
        _cache.Verify(
            c => c.RemoveAsync(CacheKeys.TicketList, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task Should_NotInvalidateCache_When_DeleteAsyncReturnsFalse()
    {
        _repository.Setup(r => r.DeleteAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var sut = CreateSut();
        var result = await sut.DeleteAsync(1);

        result.Should().BeFalse();
        _cache.Verify(
            c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    // ─── Phase 2.7 / 2.8 / 2.9 — RouteTicketAsync / ClaimTicketAsync / AssignTicketAsync ───
    //
    // These tests exercise the transactional, audit-writing branches added in steps 20-22.
    // A connection + transaction pair is mocked via IDbConnectionFactory; the service is
    // expected to call BeginTransaction once, then either Commit (success) or Rollback
    // (zero-rows-affected race / exception). Audit payload assertions parse the captured
    // PayloadJson string and check snake_case keys against §5.4 of the team-collaboration
    // proposal.

    private static readonly Guid SeedActorId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid SeedTeamId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTeamId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TargetUserId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private static Ticket TicketOnTeam(Guid teamId, Guid? assigneeId = null, int id = 7) =>
        new()
        {
            Id = id,
            Title = "T",
            Description = "D",
            Status = TicketStatus.Open,
            Priority = TicketPriority.Medium,
            ReporterId = SeedReporterId,
            TeamId = teamId,
            AssigneeId = assigneeId,
            DateCreated = DateTimeOffset.UtcNow,
            DateUpdated = DateTimeOffset.UtcNow,
        };

    /// <summary>
    /// Wires the mock <see cref="IDbConnectionFactory"/> to hand out a fresh
    /// <see cref="IDbConnection"/> whose <c>BeginTransaction()</c> returns a fresh
    /// <see cref="IDbTransaction"/>. Returns the connection and transaction so
    /// individual tests can assert <c>Commit()</c> / <c>Rollback()</c> bookkeeping.
    /// </summary>
    private (Mock<IDbConnection> Connection, Mock<IDbTransaction> Transaction) WireConnection()
    {
        var transaction = new Mock<IDbTransaction>(MockBehavior.Loose);
        var connection = new Mock<IDbConnection>(MockBehavior.Loose);
        connection.Setup(c => c.BeginTransaction()).Returns(transaction.Object);
        _connectionFactory.Setup(f => f.CreateConnection()).Returns(connection.Object);
        return (connection, transaction);
    }

    [Fact]
    public void Should_Throw_When_ConnectionFactoryIsNull()
    {
        Action act = () => new TicketService(
            _repository.Object,
            _cache.Object,
            _mapper,
            _permissions.Object,
            null!,
            _auditWriter.Object,
            _tupleWriter.Object,
            NullLogger<TicketService>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_AuditWriterIsNull()
    {
        Action act = () => new TicketService(
            _repository.Object,
            _cache.Object,
            _mapper,
            _permissions.Object,
            _connectionFactory.Object,
            null!,
            _tupleWriter.Object,
            NullLogger<TicketService>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_TupleWriterIsNull()
    {
        Action act = () => new TicketService(
            _repository.Object,
            _cache.Object,
            _mapper,
            _permissions.Object,
            _connectionFactory.Object,
            _auditWriter.Object,
            null!,
            NullLogger<TicketService>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    // ─── Happy path: route ───

    [Fact]
    public async Task RouteTicketAsync_Should_UpdateTeamAndWriteAudit_When_PermitsAllowAndTeamChanges()
    {
        var ticket = TicketOnTeam(SeedTeamId);
        _repository
            .Setup(r => r.GetByIdAsync(ticket.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ticket);
        _permissions
            .Setup(p => p.CanRouteTicketAsync(SeedActorId, ticket, OtherTeamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var (connection, transaction) = WireConnection();

        _repository
            .Setup(r => r.UpdateTeamAsync(
                connection.Object,
                transaction.Object,
                ticket.Id,
                OtherTeamId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        AuditEvent? captured = null;
        _auditWriter
            .Setup(w => w.WriteAsync(
                connection.Object,
                transaction.Object,
                It.IsAny<AuditEvent>(),
                It.IsAny<CancellationToken>()))
            .Callback<IDbConnection, IDbTransaction, AuditEvent, CancellationToken>(
                (_, _, e, _) => captured = e)
            .Returns(Task.CompletedTask);

        _cache
            .Setup(c => c.RemoveAsync(CacheKeys.TicketList, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();

        var result = await sut.RouteTicketAsync(SeedActorId, ticket.Id, OtherTeamId);

        result.Should().BeTrue();
        _repository.Verify(
            r => r.UpdateTeamAsync(
                connection.Object,
                transaction.Object,
                ticket.Id,
                OtherTeamId,
                It.IsAny<CancellationToken>()),
            Times.Once);
        _auditWriter.Verify(
            w => w.WriteAsync(
                connection.Object,
                transaction.Object,
                It.IsAny<AuditEvent>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        transaction.Verify(t => t.Commit(), Times.Once);
        transaction.Verify(t => t.Rollback(), Times.Never);
        _cache.Verify(
            c => c.RemoveAsync(CacheKeys.TicketList, It.IsAny<CancellationToken>()),
            Times.Once);

        captured.Should().NotBeNull();
        captured!.EventType.Should().Be("ticket_routed");
        captured.ActorUserId.Should().Be(SeedActorId);
        captured.Target.Should().Be(ticket.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var doc = JsonDocument.Parse(captured.PayloadJson);
        var root = doc.RootElement;
        root.GetProperty("ticket_id").GetInt32().Should().Be(ticket.Id);
        root.GetProperty("from_team_id").GetGuid().Should().Be(SeedTeamId);
        root.GetProperty("to_team_id").GetGuid().Should().Be(OtherTeamId);
        root.GetProperty("actor_id").GetGuid().Should().Be(SeedActorId);
    }

    // ─── Happy path: claim ───

    [Fact]
    public async Task ClaimTicketAsync_Should_AssignToActorAndWriteAudit_When_TicketIsUnassigned()
    {
        var ticket = TicketOnTeam(SeedTeamId, assigneeId: null);
        _repository
            .Setup(r => r.GetByIdAsync(ticket.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ticket);
        _permissions
            .Setup(p => p.CanAssignTicketAsync(SeedActorId, ticket, SeedActorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var (connection, transaction) = WireConnection();

        _repository
            .Setup(r => r.UpdateAssigneeAsync(
                connection.Object,
                transaction.Object,
                ticket.Id,
                SeedActorId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        AuditEvent? captured = null;
        _auditWriter
            .Setup(w => w.WriteAsync(
                connection.Object,
                transaction.Object,
                It.IsAny<AuditEvent>(),
                It.IsAny<CancellationToken>()))
            .Callback<IDbConnection, IDbTransaction, AuditEvent, CancellationToken>(
                (_, _, e, _) => captured = e)
            .Returns(Task.CompletedTask);

        _cache
            .Setup(c => c.RemoveAsync(CacheKeys.TicketList, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();

        var result = await sut.ClaimTicketAsync(SeedActorId, ticket.Id);

        result.Should().BeTrue();
        _repository.Verify(
            r => r.UpdateAssigneeAsync(
                connection.Object,
                transaction.Object,
                ticket.Id,
                SeedActorId,
                It.IsAny<CancellationToken>()),
            Times.Once);
        transaction.Verify(t => t.Commit(), Times.Once);

        captured.Should().NotBeNull();
        captured!.EventType.Should().Be("ticket_assigned");
        using var doc = JsonDocument.Parse(captured.PayloadJson);
        var root = doc.RootElement;
        root.GetProperty("ticket_id").GetInt32().Should().Be(ticket.Id);
        root.GetProperty("from_assignee_id").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("to_assignee_id").GetGuid().Should().Be(SeedActorId);
        root.GetProperty("actor_id").GetGuid().Should().Be(SeedActorId);
        root.GetProperty("is_self_assign").GetBoolean().Should().BeTrue();
    }

    // ─── Happy path: assign override ───

    [Fact]
    public async Task AssignTicketAsync_Should_OverrideAssigneeAndWriteAudit_When_AssigneeChanges()
    {
        var existingAssignee = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var ticket = TicketOnTeam(SeedTeamId, assigneeId: existingAssignee);
        _repository
            .Setup(r => r.GetByIdAsync(ticket.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ticket);
        _permissions
            .Setup(p => p.CanAssignTicketAsync(SeedActorId, ticket, TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var (connection, transaction) = WireConnection();

        _repository
            .Setup(r => r.UpdateAssigneeAsync(
                connection.Object,
                transaction.Object,
                ticket.Id,
                TargetUserId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        AuditEvent? captured = null;
        _auditWriter
            .Setup(w => w.WriteAsync(
                connection.Object,
                transaction.Object,
                It.IsAny<AuditEvent>(),
                It.IsAny<CancellationToken>()))
            .Callback<IDbConnection, IDbTransaction, AuditEvent, CancellationToken>(
                (_, _, e, _) => captured = e)
            .Returns(Task.CompletedTask);

        _cache
            .Setup(c => c.RemoveAsync(CacheKeys.TicketList, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();

        var result = await sut.AssignTicketAsync(SeedActorId, ticket.Id, TargetUserId);

        result.Should().BeTrue();
        captured.Should().NotBeNull();
        using var doc = JsonDocument.Parse(captured!.PayloadJson);
        var root = doc.RootElement;
        root.GetProperty("from_assignee_id").GetGuid().Should().Be(existingAssignee);
        root.GetProperty("to_assignee_id").GetGuid().Should().Be(TargetUserId);
        root.GetProperty("actor_id").GetGuid().Should().Be(SeedActorId);
        root.GetProperty("is_self_assign").GetBoolean().Should().BeFalse();
    }

    // ─── Idempotent no-ops ───

    [Fact]
    public async Task RouteTicketAsync_Should_ReturnFalseAndSkipWrites_When_DestinationEqualsCurrentTeam()
    {
        var ticket = TicketOnTeam(SeedTeamId);
        _repository
            .Setup(r => r.GetByIdAsync(ticket.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ticket);
        _permissions
            .Setup(p => p.CanRouteTicketAsync(SeedActorId, ticket, SeedTeamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = CreateSut();

        var result = await sut.RouteTicketAsync(SeedActorId, ticket.Id, SeedTeamId);

        result.Should().BeFalse();
        _connectionFactory.Verify(f => f.CreateConnection(), Times.Never);
        _auditWriter.VerifyNoOtherCalls();
        _cache.Verify(
            c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AssignTicketAsync_Should_ReturnFalseAndSkipWrites_When_TargetEqualsCurrentAssignee()
    {
        var ticket = TicketOnTeam(SeedTeamId, assigneeId: TargetUserId);
        _repository
            .Setup(r => r.GetByIdAsync(ticket.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ticket);
        _permissions
            .Setup(p => p.CanAssignTicketAsync(SeedActorId, ticket, TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = CreateSut();

        var result = await sut.AssignTicketAsync(SeedActorId, ticket.Id, TargetUserId);

        result.Should().BeFalse();
        _connectionFactory.Verify(f => f.CreateConnection(), Times.Never);
        _auditWriter.VerifyNoOtherCalls();
        _cache.Verify(
            c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ClaimTicketAsync_Should_ReturnFalse_When_TicketIsAlreadyAssignedToActor()
    {
        var ticket = TicketOnTeam(SeedTeamId, assigneeId: SeedActorId);
        _repository
            .Setup(r => r.GetByIdAsync(ticket.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ticket);
        _permissions
            .Setup(p => p.CanAssignTicketAsync(SeedActorId, ticket, SeedActorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = CreateSut();

        var result = await sut.ClaimTicketAsync(SeedActorId, ticket.Id);

        result.Should().BeFalse();
        _connectionFactory.Verify(f => f.CreateConnection(), Times.Never);
        _auditWriter.VerifyNoOtherCalls();
    }

    // ─── Missing ticket short-circuits before the gate ───

    [Fact]
    public async Task RouteTicketAsync_Should_ReturnFalseWithoutPermissionCheck_When_TicketDoesNotExist()
    {
        _repository
            .Setup(r => r.GetByIdAsync(99, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Ticket?)null);

        var sut = CreateSut();

        var result = await sut.RouteTicketAsync(SeedActorId, 99, OtherTeamId);

        result.Should().BeFalse();
        _permissions.Verify(
            p => p.CanRouteTicketAsync(
                It.IsAny<Guid>(), It.IsAny<Ticket>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _connectionFactory.Verify(f => f.CreateConnection(), Times.Never);
        _auditWriter.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AssignTicketAsync_Should_ReturnFalseWithoutPermissionCheck_When_TicketDoesNotExist()
    {
        _repository
            .Setup(r => r.GetByIdAsync(99, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Ticket?)null);

        var sut = CreateSut();

        var result = await sut.AssignTicketAsync(SeedActorId, 99, TargetUserId);

        result.Should().BeFalse();
        _permissions.Verify(
            p => p.CanAssignTicketAsync(
                It.IsAny<Guid>(), It.IsAny<Ticket>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _connectionFactory.Verify(f => f.CreateConnection(), Times.Never);
    }

    [Fact]
    public async Task ClaimTicketAsync_Should_ReturnFalseWithoutPermissionCheck_When_TicketDoesNotExist()
    {
        _repository
            .Setup(r => r.GetByIdAsync(99, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Ticket?)null);

        var sut = CreateSut();

        var result = await sut.ClaimTicketAsync(SeedActorId, 99);

        result.Should().BeFalse();
        _permissions.Verify(
            p => p.CanAssignTicketAsync(
                It.IsAny<Guid>(), It.IsAny<Ticket>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ─── Permission denied ───

    [Fact]
    public async Task RouteTicketAsync_Should_ThrowUnauthorized_When_PermissionGateDenies()
    {
        var ticket = TicketOnTeam(SeedTeamId);
        _repository
            .Setup(r => r.GetByIdAsync(ticket.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ticket);
        _permissions
            .Setup(p => p.CanRouteTicketAsync(SeedActorId, ticket, OtherTeamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var sut = CreateSut();

        Func<Task> act = () => sut.RouteTicketAsync(SeedActorId, ticket.Id, OtherTeamId);

        var ex = await act.Should().ThrowAsync<UnauthorizedAccessException>();
        ex.Which.Message.Should().Contain("route");
        _connectionFactory.Verify(f => f.CreateConnection(), Times.Never);
        _auditWriter.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AssignTicketAsync_Should_ThrowUnauthorized_When_PermissionGateDenies()
    {
        var ticket = TicketOnTeam(SeedTeamId);
        _repository
            .Setup(r => r.GetByIdAsync(ticket.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ticket);
        _permissions
            .Setup(p => p.CanAssignTicketAsync(SeedActorId, ticket, TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var sut = CreateSut();

        Func<Task> act = () => sut.AssignTicketAsync(SeedActorId, ticket.Id, TargetUserId);

        var ex = await act.Should().ThrowAsync<UnauthorizedAccessException>();
        ex.Which.Message.Should().Contain("assign");
        _connectionFactory.Verify(f => f.CreateConnection(), Times.Never);
    }

    [Fact]
    public async Task ClaimTicketAsync_Should_ThrowUnauthorized_When_PermissionGateDenies()
    {
        var ticket = TicketOnTeam(SeedTeamId);
        _repository
            .Setup(r => r.GetByIdAsync(ticket.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ticket);
        _permissions
            .Setup(p => p.CanAssignTicketAsync(SeedActorId, ticket, SeedActorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var sut = CreateSut();

        Func<Task> act = () => sut.ClaimTicketAsync(SeedActorId, ticket.Id);

        // Claim delegates to AssignTicketAsync; the exception message uses the "assign" verb.
        var ex = await act.Should().ThrowAsync<UnauthorizedAccessException>();
        ex.Which.Message.Should().Contain("assign");
    }

    // ─── Permission check parameter shape ───

    [Fact]
    public async Task RouteTicketAsync_Should_AskPermissionService_With_DestinationTeamId()
    {
        var ticket = TicketOnTeam(SeedTeamId);
        _repository
            .Setup(r => r.GetByIdAsync(ticket.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ticket);
        _permissions
            .Setup(p => p.CanRouteTicketAsync(SeedActorId, ticket, OtherTeamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var sut = CreateSut();

        Func<Task> act = () => sut.RouteTicketAsync(SeedActorId, ticket.Id, OtherTeamId);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();

        _permissions.Verify(
            p => p.CanRouteTicketAsync(SeedActorId, ticket, OtherTeamId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ClaimTicketAsync_Should_AskCanAssign_WithTargetEqualsActor()
    {
        var ticket = TicketOnTeam(SeedTeamId, assigneeId: SeedActorId); // no-op path; we just want to observe the gate call
        _repository
            .Setup(r => r.GetByIdAsync(ticket.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ticket);
        _permissions
            .Setup(p => p.CanAssignTicketAsync(SeedActorId, ticket, SeedActorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = CreateSut();

        await sut.ClaimTicketAsync(SeedActorId, ticket.Id);

        _permissions.Verify(
            p => p.CanAssignTicketAsync(SeedActorId, ticket, SeedActorId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AssignTicketAsync_Should_AskCanAssign_WithTargetUserId()
    {
        var ticket = TicketOnTeam(SeedTeamId, assigneeId: TargetUserId);
        _repository
            .Setup(r => r.GetByIdAsync(ticket.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ticket);
        _permissions
            .Setup(p => p.CanAssignTicketAsync(SeedActorId, ticket, TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = CreateSut();

        await sut.AssignTicketAsync(SeedActorId, ticket.Id, TargetUserId);

        _permissions.Verify(
            p => p.CanAssignTicketAsync(SeedActorId, ticket, TargetUserId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ─── Race: row vanished between SELECT and UPDATE ───

    [Fact]
    public async Task RouteTicketAsync_Should_RollbackAndReturnFalseWithoutAudit_When_UpdateAffectsZeroRows()
    {
        var ticket = TicketOnTeam(SeedTeamId);
        _repository
            .Setup(r => r.GetByIdAsync(ticket.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ticket);
        _permissions
            .Setup(p => p.CanRouteTicketAsync(SeedActorId, ticket, OtherTeamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var (connection, transaction) = WireConnection();

        _repository
            .Setup(r => r.UpdateTeamAsync(
                connection.Object,
                transaction.Object,
                ticket.Id,
                OtherTeamId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var sut = CreateSut();

        var result = await sut.RouteTicketAsync(SeedActorId, ticket.Id, OtherTeamId);

        result.Should().BeFalse();
        transaction.Verify(t => t.Commit(), Times.Never);
        transaction.Verify(t => t.Rollback(), Times.Once);
        _auditWriter.Verify(
            w => w.WriteAsync(
                It.IsAny<IDbConnection>(),
                It.IsAny<IDbTransaction>(),
                It.IsAny<AuditEvent>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _cache.Verify(
            c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AssignTicketAsync_Should_RollbackAndReturnFalseWithoutAudit_When_UpdateAffectsZeroRows()
    {
        var ticket = TicketOnTeam(SeedTeamId);
        _repository
            .Setup(r => r.GetByIdAsync(ticket.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ticket);
        _permissions
            .Setup(p => p.CanAssignTicketAsync(SeedActorId, ticket, TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var (connection, transaction) = WireConnection();

        _repository
            .Setup(r => r.UpdateAssigneeAsync(
                connection.Object,
                transaction.Object,
                ticket.Id,
                TargetUserId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var sut = CreateSut();

        var result = await sut.AssignTicketAsync(SeedActorId, ticket.Id, TargetUserId);

        result.Should().BeFalse();
        transaction.Verify(t => t.Commit(), Times.Never);
        transaction.Verify(t => t.Rollback(), Times.Once);
        _auditWriter.Verify(
            w => w.WriteAsync(
                It.IsAny<IDbConnection>(),
                It.IsAny<IDbTransaction>(),
                It.IsAny<AuditEvent>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ─── Cancellation token plumbing ───

    [Fact]
    public async Task RouteTicketAsync_Should_FlowCancellationToken_To_AllCollaborators()
    {
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        var ticket = TicketOnTeam(SeedTeamId);

        _repository
            .Setup(r => r.GetByIdAsync(ticket.Id, token))
            .ReturnsAsync(ticket);
        _permissions
            .Setup(p => p.CanRouteTicketAsync(SeedActorId, ticket, OtherTeamId, token))
            .ReturnsAsync(true);

        var (connection, transaction) = WireConnection();

        _repository
            .Setup(r => r.UpdateTeamAsync(connection.Object, transaction.Object, ticket.Id, OtherTeamId, token))
            .ReturnsAsync(true);
        _auditWriter
            .Setup(w => w.WriteAsync(connection.Object, transaction.Object, It.IsAny<AuditEvent>(), token))
            .Returns(Task.CompletedTask);
        _cache
            .Setup(c => c.RemoveAsync(CacheKeys.TicketList, token))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();

        var result = await sut.RouteTicketAsync(SeedActorId, ticket.Id, OtherTeamId, token);

        result.Should().BeTrue();
        _repository.Verify(r => r.GetByIdAsync(ticket.Id, token), Times.Once);
        _permissions.Verify(
            p => p.CanRouteTicketAsync(SeedActorId, ticket, OtherTeamId, token),
            Times.Once);
        _repository.Verify(
            r => r.UpdateTeamAsync(connection.Object, transaction.Object, ticket.Id, OtherTeamId, token),
            Times.Once);
        _auditWriter.Verify(
            w => w.WriteAsync(connection.Object, transaction.Object, It.IsAny<AuditEvent>(), token),
            Times.Once);
        _cache.Verify(c => c.RemoveAsync(CacheKeys.TicketList, token), Times.Once);
    }

    // ─── Phase 3.4 step 7 — tuple-write hook verification ───
    //
    // Each test below pins one branch of the create / assign flow's tuple-emission
    // contract from docs/proposals/openfga.md §3 step 7 + docs/proposals/openfga-input-contract.md §2.

    private static readonly Guid SeedProjectId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task CreateAsync_Should_EmitParentProjectAndReporterTuples_When_AssigneeIsNull()
    {
        var dto = new TicketDto
        {
            Title = "T",
            Description = "D",
            ReporterId = SeedReporterId,
            ProjectId = SeedProjectId,
            TeamId = SeedTeamId,
            AssigneeId = null,
        };
        _repository
            .Setup(r => r.CreateAsync(It.IsAny<Ticket>(), It.IsAny<CancellationToken>()))
            .Callback<Ticket, CancellationToken>((t, _) => t.Id = 42)
            .ReturnsAsync(42);
        _cache
            .Setup(c => c.RemoveAsync(CacheKeys.TicketList, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        IReadOnlyList<TupleKey>? capturedWrites = null;
        IReadOnlyList<TupleKey>? capturedDeletes = null;
        _tupleWriter
            .Setup(w => w.WriteAsync(
                It.IsAny<IReadOnlyList<TupleKey>>(),
                It.IsAny<IReadOnlyList<TupleKey>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<TupleKey>, IReadOnlyList<TupleKey>, CancellationToken>(
                (writes, deletes, _) =>
                {
                    capturedWrites = writes;
                    capturedDeletes = deletes;
                })
            .Returns(Task.CompletedTask);

        var sut = CreateSut();

        await sut.CreateAsync(dto);

        capturedWrites.Should().NotBeNull();
        capturedWrites!.Should().HaveCount(2);
        capturedWrites.Should().ContainSingle(t => t.Relation == TupleShapes.ParentProjectRelation);
        capturedWrites.Should().ContainSingle(t => t.Relation == TupleShapes.ReporterRelation);
        capturedWrites.Should().NotContain(t => t.Relation == TupleShapes.AssigneeRelation);
        capturedDeletes.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_Should_AlsoEmitAssigneeTuple_When_AssigneeIsSet()
    {
        var dto = new TicketDto
        {
            Title = "T",
            Description = "D",
            ReporterId = SeedReporterId,
            ProjectId = SeedProjectId,
            TeamId = SeedTeamId,
            AssigneeId = TargetUserId,
        };
        _repository
            .Setup(r => r.CreateAsync(It.IsAny<Ticket>(), It.IsAny<CancellationToken>()))
            .Callback<Ticket, CancellationToken>((t, _) => t.Id = 99)
            .ReturnsAsync(99);
        _cache
            .Setup(c => c.RemoveAsync(CacheKeys.TicketList, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        IReadOnlyList<TupleKey>? capturedWrites = null;
        _tupleWriter
            .Setup(w => w.WriteAsync(
                It.IsAny<IReadOnlyList<TupleKey>>(),
                It.IsAny<IReadOnlyList<TupleKey>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<TupleKey>, IReadOnlyList<TupleKey>, CancellationToken>(
                (writes, _, _) => capturedWrites = writes)
            .Returns(Task.CompletedTask);

        var sut = CreateSut();

        await sut.CreateAsync(dto);

        capturedWrites.Should().NotBeNull();
        capturedWrites!.Should().HaveCount(3);
        capturedWrites.Should().ContainSingle(t => t.Relation == TupleShapes.ParentProjectRelation);
        capturedWrites.Should().ContainSingle(t => t.Relation == TupleShapes.ReporterRelation);
        capturedWrites
            .Should()
            .ContainSingle(t =>
                t.Relation == TupleShapes.AssigneeRelation && t.User == $"user:{TargetUserId:D}");
    }

    [Fact]
    public async Task AssignTicketAsync_Should_EmitAtomicReplace_When_AssigneeChanges()
    {
        var existingAssignee = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var ticket = TicketOnTeam(SeedTeamId, assigneeId: existingAssignee);
        _repository
            .Setup(r => r.GetByIdAsync(ticket.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ticket);
        _permissions
            .Setup(p => p.CanAssignTicketAsync(SeedActorId, ticket, TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var (connection, transaction) = WireConnection();
        _repository
            .Setup(r => r.UpdateAssigneeAsync(connection.Object, transaction.Object, ticket.Id, TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _auditWriter
            .Setup(w => w.WriteAsync(connection.Object, transaction.Object, It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _cache
            .Setup(c => c.RemoveAsync(CacheKeys.TicketList, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        TupleKey? capturedDelete = null;
        TupleKey? capturedWrite = null;
        _tupleWriter
            .Setup(w => w.ReplaceAsync(It.IsAny<TupleKey?>(), It.IsAny<TupleKey?>(), It.IsAny<CancellationToken>()))
            .Callback<TupleKey?, TupleKey?, CancellationToken>((d, w, _) =>
            {
                capturedDelete = d;
                capturedWrite = w;
            })
            .Returns(Task.CompletedTask);

        var sut = CreateSut();

        await sut.AssignTicketAsync(SeedActorId, ticket.Id, TargetUserId);

        _tupleWriter.Verify(
            w => w.ReplaceAsync(It.IsAny<TupleKey?>(), It.IsAny<TupleKey?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        capturedDelete.Should().NotBeNull();
        capturedDelete!.Relation.Should().Be(TupleShapes.AssigneeRelation);
        capturedDelete.User.Should().Be($"user:{existingAssignee:D}");
        capturedWrite.Should().NotBeNull();
        capturedWrite!.Relation.Should().Be(TupleShapes.AssigneeRelation);
        capturedWrite.User.Should().Be($"user:{TargetUserId:D}");
    }

    [Fact]
    public async Task AssignTicketAsync_Should_EmitWriteOnly_When_PreviouslyUnassigned()
    {
        var ticket = TicketOnTeam(SeedTeamId, assigneeId: null);
        _repository
            .Setup(r => r.GetByIdAsync(ticket.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ticket);
        _permissions
            .Setup(p => p.CanAssignTicketAsync(SeedActorId, ticket, TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var (connection, transaction) = WireConnection();
        _repository
            .Setup(r => r.UpdateAssigneeAsync(connection.Object, transaction.Object, ticket.Id, TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _auditWriter
            .Setup(w => w.WriteAsync(connection.Object, transaction.Object, It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _cache
            .Setup(c => c.RemoveAsync(CacheKeys.TicketList, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        TupleKey? capturedDelete = null;
        TupleKey? capturedWrite = null;
        _tupleWriter
            .Setup(w => w.ReplaceAsync(It.IsAny<TupleKey?>(), It.IsAny<TupleKey?>(), It.IsAny<CancellationToken>()))
            .Callback<TupleKey?, TupleKey?, CancellationToken>((d, w, _) =>
            {
                capturedDelete = d;
                capturedWrite = w;
            })
            .Returns(Task.CompletedTask);

        var sut = CreateSut();

        await sut.AssignTicketAsync(SeedActorId, ticket.Id, TargetUserId);

        capturedDelete.Should().BeNull();
        capturedWrite.Should().NotBeNull();
        capturedWrite!.User.Should().Be($"user:{TargetUserId:D}");
    }

    [Fact]
    public async Task AssignTicketAsync_Should_NotEmitTuple_When_NoOpReassignment()
    {
        // Re-assigning to the current assignee is the idempotent no-op path —
        // no DB UPDATE, no audit row, and crucially no tuple write either.
        var ticket = TicketOnTeam(SeedTeamId, assigneeId: TargetUserId);
        _repository
            .Setup(r => r.GetByIdAsync(ticket.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ticket);
        _permissions
            .Setup(p => p.CanAssignTicketAsync(SeedActorId, ticket, TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = CreateSut();

        await sut.AssignTicketAsync(SeedActorId, ticket.Id, TargetUserId);

        _tupleWriter.Verify(
            w => w.ReplaceAsync(It.IsAny<TupleKey?>(), It.IsAny<TupleKey?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _tupleWriter.Verify(
            w => w.WriteAsync(It.IsAny<IReadOnlyList<TupleKey>>(), It.IsAny<IReadOnlyList<TupleKey>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
