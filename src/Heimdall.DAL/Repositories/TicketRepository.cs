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

    private const string SelectColumns =
        "id AS Id, title AS Title, description AS Description, status AS Status, "
        + "priority AS Priority, reporter AS Reporter, assignee AS Assignee, "
        + "date_created AS DateCreated, date_updated AS DateUpdated";

    /// <inheritdoc />
    public async Task<IReadOnlyList<Ticket>> GetAllAsync(
        CancellationToken cancellationToken = default
    )
    {
        using var connection = CreateConnection();
        var command = new CommandDefinition(
            $"SELECT {SelectColumns} FROM tickets ORDER BY date_created DESC, id DESC",
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
        string querySql =
            $"""
            SELECT {SelectColumns}
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
        const string sql =
            @"
INSERT INTO tickets
    (title, description, status, priority, reporter, assignee, date_created, date_updated)
VALUES
    (@Title, @Description, @Status, @Priority, @Reporter, @Assignee, @DateCreated, @DateUpdated)
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
        const string sql =
            @"
UPDATE tickets SET
    title        = @Title,
    description  = @Description,
    status       = @Status,
    priority     = @Priority,
    reporter     = @Reporter,
    assignee     = @Assignee,
    date_updated = @DateUpdated
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
