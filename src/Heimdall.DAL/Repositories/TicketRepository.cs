using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Dapper.Extensions;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Heimdall.Core.Models.Pagination;
using Heimdall.DAL.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Heimdall.DAL.Repositories;

/// <summary>
/// Dapper implementation of <see cref="ITicketRepository"/>. Paged reads are routed
/// through <see cref="IDapper"/> from <c>Dapper.Extensions.PostgreSQL</c>; all other operations
/// use a directly-managed <see cref="NpgsqlConnection"/> for the smallest possible footprint.
/// </summary>
public class TicketRepository : ITicketRepository
{
    // Full column projection — used when the consumer needs the long-form description
    // (e.g. the edit page / GetByIdAsync). description is a TEXT column and may be
    // TOAST-stored, so omitting it from list queries materially reduces row width and
    // detoasting work.
    private const string SelectColumns =
        "id AS Id, title AS Title, description AS Description, status AS Status, "
        + "priority AS Priority, reporter AS Reporter, assignee AS Assignee, "
        + "date_created AS DateCreated, date_updated AS DateUpdated";

    // List projection — excludes description because the Tickets list page does not
    // render it. Description on the resulting Ticket entity defaults to string.Empty,
    // which is correct for any list-only consumer (the cached list DTO never reads it).
    private const string ListSelectColumns =
        "id AS Id, title AS Title, status AS Status, priority AS Priority, "
        + "reporter AS Reporter, assignee AS Assignee, "
        + "date_created AS DateCreated, date_updated AS DateUpdated";

    // Defensive upper bound on the unfiltered "list everything" query. The cached list
    // is intentionally simple (single key, 5-min TTL) and is not designed to materialise
    // arbitrarily large result sets in process memory — cap it at a sane ceiling so a
    // run-away ticket count cannot OOM the app or thrash the cache payload size.
    private const int GetAllRowCap = 500;

    private readonly string _connectionString;
    private readonly IDapper? _dapper;

    /// <summary>
    /// Initializes a new instance using only <see cref="DataOptions"/>. Paged reads will fall
    /// back to a self-managed <see cref="NpgsqlConnection"/> + <c>QueryMultipleAsync</c> path,
    /// which is suitable for unit / integration tests that don't wire up Dapper.Extensions DI.
    /// </summary>
    /// <param name="options">Bound <see cref="DataOptions"/> providing the Postgres connection string.</param>
    public TicketRepository(IOptions<DataOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = options.Value.PostgresConnectionString;
        _dapper = null;
    }

    /// <summary>
    /// Initializes a new instance using <see cref="IDapper"/> for paged reads. Autofac will
    /// prefer this constructor at runtime because it is the greediest resolvable overload.
    /// </summary>
    /// <param name="options">Bound <see cref="DataOptions"/> providing the Postgres connection string.</param>
    /// <param name="dapper">The Dapper.Extensions handle used for paged queries.</param>
    public TicketRepository(IOptions<DataOptions> options, IDapper dapper)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dapper);
        _connectionString = options.Value.PostgresConnectionString;
        _dapper = dapper;
    }

    private IDbConnection CreateConnection() => new NpgsqlConnection(_connectionString);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Ticket>> GetAllAsync(
        CancellationToken cancellationToken = default
    )
    {
        using var connection = CreateConnection();
        var command = new CommandDefinition(
            $"SELECT {ListSelectColumns} FROM tickets ORDER BY date_created DESC, id DESC LIMIT {GetAllRowCap}",
            cancellationToken: cancellationToken
        );
        var rows = await connection.QueryAsync<Ticket>(command).ConfigureAwait(false);
        return [.. rows];
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<Ticket> Items, int TotalCount)> GetPagedAsync(
        PagedQuery query,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(query);

        // Resolve the sort column from the allow-list — never interpolate user input directly.
        string sortColumn = PagedQuery.AllowedSortFields.TryGetValue(query.SortField, out string? mappedColumn)
            ? mappedColumn
            : "date_created";

        string sortDirection = query.SortDirection == SortDirection.Descending ? "DESC" : "ASC";

        var parameters = new DynamicParameters();
        string whereClause = string.Empty;
        if (!string.IsNullOrEmpty(query.SearchText))
        {
            whereClause =
                "WHERE (title ILIKE @Search OR description ILIKE @Search OR reporter ILIKE @Search OR COALESCE(assignee, '') ILIKE @Search)";
            parameters.Add("Search", $"%{query.SearchText}%");
        }

        // ORDER BY always includes "id DESC" as a deterministic tie-breaker so paging is stable
        // across requests even when the sort column has duplicate values.
        // Description is excluded from the list projection — the Tickets page does not render
        // it, and TEXT is detoasted on read which is wasted work for the table view.
        string querySql =
            $"""
            SELECT {ListSelectColumns}
            FROM   tickets
            {whereClause}
            ORDER  BY {sortColumn} {sortDirection}, id DESC
            LIMIT  @Take OFFSET @Skip
            """;

        string countSql =
            $"""
            SELECT COUNT(*)::int
            FROM   tickets
            {whereClause}
            """;

        if (_dapper is not null)
        {
            // Dapper.Extensions automatically supplies @Skip and @Take based on pageindex/pageSize.
            var page = await _dapper
                .QueryPageAsync<Ticket>(
                    countSql,
                    querySql,
                    query.Page,
                    query.PageSize,
                    parameters,
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);

            var items = page.Result ?? new List<Ticket>();
            return (items.AsReadOnly(), checked((int)page.TotalCount));
        }

        // Fallback path used when no IDapper is wired up (e.g. some integration tests).
        parameters.Add("Take", query.PageSize);
        parameters.Add("Skip", ((long)query.Page - 1L) * query.PageSize);

        string combinedSql = querySql + ";\n" + countSql + ";";
        using var connection = CreateConnection();
        var command = new CommandDefinition(combinedSql, parameters, cancellationToken: cancellationToken);

        using var multi = await connection.QueryMultipleAsync(command).ConfigureAwait(false);
        var rows = await multi.ReadAsync<Ticket>().ConfigureAwait(false);
        int totalCount = await multi.ReadSingleAsync<int>().ConfigureAwait(false);

        return ([.. rows], totalCount);
    }

    /// <inheritdoc />
    public async Task<Ticket?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default
    )
    {
        using var connection = CreateConnection();
        var command = new CommandDefinition(
            $"SELECT {SelectColumns} FROM tickets WHERE id = @Id",
            new { Id = id },
            cancellationToken: cancellationToken
        );
        return await connection
            .QuerySingleOrDefaultAsync<Ticket>(command)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CreateAsync(
        Ticket ticket,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(ticket);
        using var connection = CreateConnection();
        // status / priority columns are SMALLINT; explicit ::smallint casts keep the parameter
        // type the planner sees stable (Dapper binds the int-backed enums as int4 by default).
        const string sql =
            @"
INSERT INTO tickets
    (title, description, status, priority, reporter, assignee, date_created, date_updated)
VALUES
    (@Title, @Description, @Status::smallint, @Priority::smallint, @Reporter, @Assignee, @DateCreated, @DateUpdated)
RETURNING id;";
        var command = new CommandDefinition(sql, ticket, cancellationToken: cancellationToken);
        var id = await connection.ExecuteScalarAsync<int>(command).ConfigureAwait(false);
        ticket.Id = id;
        return id;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        Ticket ticket,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(ticket);
        using var connection = CreateConnection();
        // date_created is intentionally not in the SET list — it is immutable after INSERT.
        // date_updated is set to NOW() server-side so the timestamp is sourced from the
        // database clock (consistent with the column's DEFAULT now()) rather than an
        // arbitrary client clock — avoids skew between hosts and removes one
        // round-trip-worth of "what time is it?" reasoning from the BLL.
        // status / priority are SMALLINT; cast explicitly so the planner sees a stable type.
        const string sql =
            @"
UPDATE tickets SET
    title        = @Title,
    description  = @Description,
    status       = @Status::smallint,
    priority     = @Priority::smallint,
    reporter     = @Reporter,
    assignee     = @Assignee,
    date_updated = now()
WHERE id = @Id;";
        var command = new CommandDefinition(sql, ticket, cancellationToken: cancellationToken);
        var rows = await connection.ExecuteAsync(command).ConfigureAwait(false);
        return rows > 0;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection();
        var command = new CommandDefinition(
            "DELETE FROM tickets WHERE id = @Id",
            new { Id = id },
            cancellationToken: cancellationToken
        );
        var rows = await connection.ExecuteAsync(command).ConfigureAwait(false);
        return rows > 0;
    }
}
