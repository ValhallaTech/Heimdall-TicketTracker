using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.BLL.Authorization;
using Heimdall.BLL.Mapping;
using Heimdall.Core.Auditing;
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

    /// <summary>
    /// Cached <see cref="JsonSerializerOptions"/> used to serialize audit-event payloads
    /// into their <c>jsonb</c> wire form per <c>docs/proposals/team-collaboration.md</c>
    /// §5.4. snake_case property naming matches the column conventions used elsewhere
    /// in the audit-events feed so downstream consumers (Seq, future SIEM, the admin
    /// audit panel) see a single, consistent shape.
    /// </summary>
    private static readonly JsonSerializerOptions AuditPayloadJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly ITicketRepository _repository;
    private readonly ICacheService _cache;
    private readonly ITicketMapper _mapper;
    private readonly IPermissionService _permissions;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IAuditEventWriter _auditWriter;
    private readonly ILogger<TicketService> _logger;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="repository">Ticket persistence abstraction.</param>
    /// <param name="cache">Distributed cache used for read-through list caching.</param>
    /// <param name="mapper">Mapster-generated mapper used to project between entities and DTOs.</param>
    /// <param name="permissions">
    /// Authorization seam (<c>docs/proposals/team-collaboration.md</c> §6). Captured here in
    /// Phase 2.6 step 19; the route / claim / assign enforcement that consumes it landed
    /// in Phase 2.7 (steps 20–22).
    /// </param>
    /// <param name="connectionFactory">
    /// Factory used to open the connection that owns the route / claim / assign
    /// transaction. Per <c>docs/proposals/team-collaboration.md</c> §5.4, the
    /// <c>tickets</c> UPDATE and the <c>audit_events</c> INSERT must commit (or roll
    /// back) atomically — so this service opens the connection, begins the transaction,
    /// and threads both into the participating repositories.
    /// </param>
    /// <param name="auditWriter">
    /// Append-only writer used to record <c>ticket_routed</c> and <c>ticket_assigned</c>
    /// events on the same transaction as the <c>tickets</c> mutation.
    /// </param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when any required dependency is <see langword="null"/>.
    /// </exception>
    public TicketService(
        ITicketRepository repository,
        ICacheService cache,
        ITicketMapper mapper,
        IPermissionService permissions,
        IDbConnectionFactory connectionFactory,
        IAuditEventWriter auditWriter,
        ILogger<TicketService> logger
    )
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(auditWriter);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _cache = cache;
        _mapper = mapper;
        _permissions = permissions;
        _connectionFactory = connectionFactory;
        _auditWriter = auditWriter;
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

    /// <inheritdoc />
    public async Task<bool> RouteTicketAsync(
        Guid actorId,
        int ticketId,
        Guid destinationTeamId,
        CancellationToken cancellationToken = default
    )
    {
        Ticket? ticket = await _repository
            .GetByIdAsync(ticketId, cancellationToken)
            .ConfigureAwait(false);
        if (ticket is null)
        {
            return false;
        }

        bool allowed = await _permissions
            .CanRouteTicketAsync(actorId, ticket, destinationTeamId, cancellationToken)
            .ConfigureAwait(false);
        if (!allowed)
        {
            _logger.LogWarning(
                "Route denied for ticket {TicketId} to team {DestinationTeamId}.",
                ticketId,
                destinationTeamId
            );
            throw new UnauthorizedAccessException(
                $"User {actorId} is not permitted to route ticket {ticketId}."
            );
        }

        // Idempotent no-op: routing to the team the ticket already lives on is a UI
        // race, not a domain event — silently skip without emitting an audit row.
        if (ticket.TeamId == destinationTeamId)
        {
            return false;
        }

        Guid fromTeamId = ticket.TeamId;
        TicketRoutedPayload payload = new(ticketId, fromTeamId, destinationTeamId, actorId);
        AuditEvent auditEvent = new()
        {
            ActorUserId = actorId,
            EventType = "ticket_routed",
            Target = ticketId.ToString(CultureInfo.InvariantCulture),
            PayloadJson = JsonSerializer.Serialize(payload, AuditPayloadJsonOptions),
        };

        bool applied = await ApplyTransactionalUpdateAsync(
            (connection, transaction, ct) =>
                _repository.UpdateTeamAsync(
                    connection,
                    transaction,
                    ticketId,
                    destinationTeamId,
                    ct
                ),
            auditEvent,
            cancellationToken
        ).ConfigureAwait(false);

        if (applied)
        {
            await _cache.RemoveAsync(ListCacheKey, cancellationToken).ConfigureAwait(false);
        }

        return applied;
    }

    /// <inheritdoc />
    public Task<bool> ClaimTicketAsync(
        Guid actorId,
        int ticketId,
        CancellationToken cancellationToken = default
    ) =>
        // A claim is a self-assign: the gate per §5.3 is the same CanAssignTicketAsync
        // matrix with targetUserId == actorId. Delegate so there's exactly one
        // implementation path.
        AssignTicketAsync(actorId, ticketId, actorId, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> AssignTicketAsync(
        Guid actorId,
        int ticketId,
        Guid targetUserId,
        CancellationToken cancellationToken = default
    )
    {
        Ticket? ticket = await _repository
            .GetByIdAsync(ticketId, cancellationToken)
            .ConfigureAwait(false);
        if (ticket is null)
        {
            return false;
        }

        bool allowed = await _permissions
            .CanAssignTicketAsync(actorId, ticket, targetUserId, cancellationToken)
            .ConfigureAwait(false);
        if (!allowed)
        {
            _logger.LogWarning(
                "Assign denied for ticket {TicketId} to user {TargetUserId}.",
                ticketId,
                targetUserId
            );
            throw new UnauthorizedAccessException(
                $"User {actorId} is not permitted to assign ticket {ticketId}."
            );
        }

        // Idempotent no-op: re-assigning to the current assignee is a UI race.
        if (ticket.AssigneeId == targetUserId)
        {
            return false;
        }

        Guid? fromAssigneeId = ticket.AssigneeId;
        bool isSelfAssign = targetUserId == actorId;
        TicketAssignedPayload payload = new(
            ticketId,
            fromAssigneeId,
            targetUserId,
            actorId,
            isSelfAssign
        );
        AuditEvent auditEvent = new()
        {
            ActorUserId = actorId,
            EventType = "ticket_assigned",
            Target = ticketId.ToString(CultureInfo.InvariantCulture),
            PayloadJson = JsonSerializer.Serialize(payload, AuditPayloadJsonOptions),
        };

        bool applied = await ApplyTransactionalUpdateAsync(
            (connection, transaction, ct) =>
                _repository.UpdateAssigneeAsync(
                    connection,
                    transaction,
                    ticketId,
                    targetUserId,
                    ct
                ),
            auditEvent,
            cancellationToken
        ).ConfigureAwait(false);

        if (applied)
        {
            await _cache.RemoveAsync(ListCacheKey, cancellationToken).ConfigureAwait(false);
        }

        return applied;
    }

    /// <summary>
    /// Opens a connection, begins a transaction, runs the supplied narrow UPDATE, writes
    /// the audit-event row on the same transaction, and commits. Rolls back and
    /// rethrows on any exception. Returns <see langword="false"/> (and rolls back, no
    /// audit row) when the UPDATE reports zero rows affected — the row vanished
    /// between the load and the UPDATE.
    /// </summary>
    /// <param name="updateAsync">Narrow UPDATE delegate; must return <c>true</c> on success.</param>
    /// <param name="auditEvent">Audit-event row to insert on the same transaction.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    private async Task<bool> ApplyTransactionalUpdateAsync(
        Func<System.Data.IDbConnection, System.Data.IDbTransaction, CancellationToken, Task<bool>> updateAsync,
        AuditEvent auditEvent,
        CancellationToken cancellationToken
    )
    {
        using System.Data.IDbConnection connection = _connectionFactory.CreateConnection();
        connection.Open();
        using System.Data.IDbTransaction transaction = connection.BeginTransaction();
        try
        {
            bool updated = await updateAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            if (!updated)
            {
                transaction.Rollback();
                return false;
            }

            await _auditWriter
                .WriteAsync(connection, transaction, auditEvent, cancellationToken)
                .ConfigureAwait(false);
            transaction.Commit();
            return true;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Structured payload for the <c>ticket_routed</c> audit event
    /// (<c>docs/proposals/team-collaboration.md</c> §5.4). Serialized to snake_case
    /// JSON via <see cref="AuditPayloadJsonOptions"/>.
    /// </summary>
    private sealed record TicketRoutedPayload(
        int TicketId,
        Guid FromTeamId,
        Guid ToTeamId,
        Guid ActorId
    );

    /// <summary>
    /// Structured payload for the <c>ticket_assigned</c> audit event
    /// (<c>docs/proposals/team-collaboration.md</c> §5.4). <c>FromAssigneeId</c> and
    /// <c>ToAssigneeId</c> are nullable to model the legitimate unassigned state.
    /// </summary>
    private sealed record TicketAssignedPayload(
        int TicketId,
        Guid? FromAssigneeId,
        Guid? ToAssigneeId,
        Guid ActorId,
        bool IsSelfAssign
    );

    /// <summary>Wrapper so lists can be serialized as a single cached JSON object.</summary>
    private sealed record CachedList(IReadOnlyList<TicketDto> Items);
}
