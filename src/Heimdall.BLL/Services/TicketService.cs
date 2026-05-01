using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.BLL.Mapping;
using Heimdall.Core.Caching;
using Heimdall.Core.Dtos;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Heimdall.Core.Models.Pagination;
using Microsoft.Extensions.Logging;

namespace Heimdall.BLL.Services;

/// <summary>
/// Default <see cref="ITicketService"/> implementation that coordinates the repository and
/// cache. Lists are cached with a short TTL and invalidated on writes.
/// </summary>
public class TicketService : ITicketService
{
    private const string ListCacheKey = CacheKeys.TicketList;
    private static readonly TimeSpan ListCacheTtl = TimeSpan.FromMinutes(5);

    private readonly ITicketRepository _repository;
    private readonly ICacheService _cache;
    private readonly ITicketMapper _mapper;
    private readonly ILogger<TicketService> _logger;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="repository">Ticket persistence abstraction.</param>
    /// <param name="cache">Distributed cache used for read-through list caching.</param>
    /// <param name="mapper">Mapster-generated mapper used to project between entities and DTOs.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when any required dependency is <see langword="null"/>.
    /// </exception>
    public TicketService(
        ITicketRepository repository,
        ICacheService cache,
        ITicketMapper mapper,
        ILogger<TicketService> logger
    )
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _cache = cache;
        _mapper = mapper;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TicketDto>> GetAllAsync(
        CancellationToken cancellationToken = default
    )
    {
        var cached = await _cache
            .GetAsync<CachedList>(ListCacheKey, cancellationToken)
            .ConfigureAwait(false);
        if (cached is not null)
        {
            _logger.LogDebug("Ticket list served from cache ({Count} rows).", cached.Items.Count);
            return cached.Items;
        }

        var entries = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var dtos = _mapper.Map(entries);
        await _cache
            .SetAsync(ListCacheKey, new CachedList(dtos), ListCacheTtl, cancellationToken)
            .ConfigureAwait(false);
        return dtos;
    }

    /// <inheritdoc />
    public async Task<PagedResult<TicketDto>> GetPagedAsync(
        PagedQuery query,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(query);

        var sanitized = query.Sanitized();
        var (items, totalCount) = await _repository
            .GetPagedAsync(sanitized, cancellationToken)
            .ConfigureAwait(false);

        var dtos = _mapper.Map(items);

        PagedResult<TicketDto> result = new()
        {
            Items = dtos,
            TotalCount = totalCount,
            Page = sanitized.Page,
            PageSize = sanitized.PageSize,
        };

        _logger.LogDebug(
            "GetPagedAsync returned page {Page}/{TotalPages} ({PageSize} items per page, {TotalCount} total).",
            result.Page,
            result.TotalPages,
            result.PageSize,
            result.TotalCount
        );

        return result;
    }

    /// <inheritdoc />
    public async Task<TicketDto?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default
    )
    {
        var entry = await _repository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        return entry is null ? null : _mapper.Map(entry);
    }

    /// <inheritdoc />
    public async Task<TicketDto> CreateAsync(
        TicketDto dto,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(dto);
        var ticket = _mapper.Map(dto);
        var now = DateTimeOffset.UtcNow;
        ticket.DateCreated = now;
        ticket.DateUpdated = now;
        await _repository.CreateAsync(ticket, cancellationToken).ConfigureAwait(false);
        await _cache.RemoveAsync(ListCacheKey, cancellationToken).ConfigureAwait(false);
        return _mapper.Map(ticket);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        TicketDto dto,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(dto);

        // Single-round-trip update. The repository's UPDATE is keyed by Id and returns
        // rowsAffected, so a separate get-by-id existence check is redundant — and worse,
        // would open a TOCTOU window where the row could be deleted between the SELECT and
        // the UPDATE. Map fresh: the TicketDto -> Ticket profile ignores DateCreated /
        // DateUpdated, and the UPDATE statement neither reads nor writes DateCreated and
        // sources DateUpdated from now() server-side, so we never overwrite the original
        // creation timestamp.
        var ticket = _mapper.Map(dto);
        var updated = await _repository
            .UpdateAsync(ticket, cancellationToken)
            .ConfigureAwait(false);
        if (updated)
        {
            await _cache.RemoveAsync(ListCacheKey, cancellationToken).ConfigureAwait(false);
        }

        return updated;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var deleted = await _repository.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        if (deleted)
        {
            await _cache.RemoveAsync(ListCacheKey, cancellationToken).ConfigureAwait(false);
        }

        return deleted;
    }

    /// <summary>Wrapper so lists can be serialized as a single cached JSON object.</summary>
    private sealed record CachedList(IReadOnlyList<TicketDto> Items);
}
