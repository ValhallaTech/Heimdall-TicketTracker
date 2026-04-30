using AutoMapper;
using FluentAssertions;
using Heimdall.BLL.Mapping;
using Heimdall.BLL.Services;
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
    private readonly IMapper _mapper;

    public TicketServiceTests()
    {
        var config = new MapperConfiguration(cfg => cfg.AddProfile<TicketProfile>(), loggerFactory: NullLoggerFactory.Instance);
        _mapper = config.CreateMapper();
    }

    private TicketService CreateSut() =>
        new(_repository.Object, _cache.Object, _mapper, NullLogger<TicketService>.Instance);

    private static Ticket SampleTicket(int id = 1) => new()
    {
        Id = id,
        Title = $"Title {id}",
        Description = $"Desc {id}",
        Status = TicketStatus.Open,
        Priority = TicketPriority.Medium,
        Reporter = "Reporter",
        Assignee = null,
        DateCreated = DateTimeOffset.UtcNow,
        DateUpdated = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Should_Throw_When_RepositoryIsNull()
    {
        Action act = () => new TicketService(null!, _cache.Object, _mapper, NullLogger<TicketService>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_CacheIsNull()
    {
        Action act = () => new TicketService(_repository.Object, null!, _mapper, NullLogger<TicketService>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_MapperIsNull()
    {
        Action act = () => new TicketService(_repository.Object, _cache.Object, null!, NullLogger<TicketService>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_LoggerIsNull()
    {
        Action act = () => new TicketService(_repository.Object, _cache.Object, _mapper, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_ReturnRepositoryRowsAndPopulateCache_When_GetAllAsyncMisses()
    {
        var tickets = new[] { SampleTicket(1), SampleTicket(2) };
        var cache = new RecordingCacheService();
        _repository.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(tickets);

        var sut = new TicketService(_repository.Object, cache, _mapper, NullLogger<TicketService>.Instance);

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
        _repository.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new[] { SampleTicket(99) });

        var sut = new TicketService(_repository.Object, cache, _mapper, NullLogger<TicketService>.Instance);

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

        public Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
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
        _repository.Setup(r => r.GetByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync((Ticket?)null);
        var sut = CreateSut();

        var result = await sut.GetByIdAsync(7);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Should_ReturnDto_When_GetByIdAsyncHits()
    {
        var ticket = SampleTicket(7);
        _repository.Setup(r => r.GetByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(ticket);
        var sut = CreateSut();

        var result = await sut.GetByIdAsync(7);

        result.Should().NotBeNull();
        result!.Id.Should().Be(7);
    }

    [Fact]
    public async Task Should_CreateAndInvalidateCache_When_CreateAsyncCalled()
    {
        var dto = new TicketDto { Title = "T", Description = "D", Reporter = "r" };
        Ticket? saved = null;
        _repository
            .Setup(r => r.CreateAsync(It.IsAny<Ticket>(), It.IsAny<CancellationToken>()))
            .Callback<Ticket, CancellationToken>((t, _) => { saved = t; t.Id = 11; })
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
        _cache.Verify(c => c.RemoveAsync(CacheKeys.TicketList, It.IsAny<CancellationToken>()), Times.Once);
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
        var dto = new TicketDto { Id = 1, Title = "T", Description = "D", Reporter = "r" };
        _repository.Setup(r => r.UpdateAsync(It.IsAny<Ticket>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _cache.Setup(c => c.RemoveAsync(CacheKeys.TicketList, It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        var sut = CreateSut();
        var result = await sut.UpdateAsync(dto);

        result.Should().BeTrue();
        _cache.Verify(c => c.RemoveAsync(CacheKeys.TicketList, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Should_NotInvalidateCache_When_UpdateAsyncReturnsFalse()
    {
        var dto = new TicketDto { Id = 1, Title = "T", Description = "D", Reporter = "r" };
        _repository.Setup(r => r.UpdateAsync(It.IsAny<Ticket>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var sut = CreateSut();
        var result = await sut.UpdateAsync(dto);

        result.Should().BeFalse();
        _cache.Verify(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
        _cache.Setup(c => c.RemoveAsync(CacheKeys.TicketList, It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        var sut = CreateSut();
        var result = await sut.DeleteAsync(1);

        result.Should().BeTrue();
        _cache.Verify(c => c.RemoveAsync(CacheKeys.TicketList, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Should_NotInvalidateCache_When_DeleteAsyncReturnsFalse()
    {
        _repository.Setup(r => r.DeleteAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var sut = CreateSut();
        var result = await sut.DeleteAsync(1);

        result.Should().BeFalse();
        _cache.Verify(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
