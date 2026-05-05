using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Heimdall.DAL.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Heimdall.DAL.Repositories;

/// <summary>
/// Dapper implementation of <see cref="ITeamRepository"/>. See
/// <see cref="OrganizationRepository"/> for the conventions this class follows
/// (including the <c>citext</c> → <see cref="string"/> projection workaround).
/// </summary>
public sealed class TeamRepository : ITeamRepository
{
    private const string SelectColumns =
        "id AS Id, organization_id AS OrganizationId, slug::text AS Slug, "
        + "name AS Name, created_at AS CreatedAt, created_by AS CreatedBy";

    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="TeamRepository"/> class.
    /// </summary>
    /// <param name="options">Bound <see cref="DataOptions"/> providing the Postgres connection string.</param>
    public TeamRepository(IOptions<DataOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = options.Value.PostgresConnectionString;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Team>> GetByOrganizationAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            $"SELECT {SelectColumns} FROM teams WHERE organization_id = @OrganizationId ORDER BY slug ASC",
            new { OrganizationId = organizationId },
            cancellationToken: cancellationToken
        );
        var rows = await connection.QueryAsync<Team>(command).ConfigureAwait(false);
        return [.. rows];
    }

    /// <inheritdoc />
    public async Task<Team?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            $"SELECT {SelectColumns} FROM teams WHERE id = @Id",
            new { Id = id },
            cancellationToken: cancellationToken
        );
        return await connection.QuerySingleOrDefaultAsync<Team>(command).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Team?> GetBySlugAsync(
        Guid organizationId,
        string slug,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        await using var connection = new NpgsqlConnection(_connectionString);
        // ::citext cast — see OrganizationRepository.GetBySlugAsync for rationale.
        var command = new CommandDefinition(
            $"SELECT {SelectColumns} FROM teams WHERE organization_id = @OrganizationId AND slug = @Slug::citext",
            new { OrganizationId = organizationId, Slug = slug },
            cancellationToken: cancellationToken
        );
        return await connection.QuerySingleOrDefaultAsync<Team>(command).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Guid> CreateAsync(
        Team team,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(team);
        const string sql = @"
INSERT INTO teams (organization_id, slug, name, created_by)
VALUES (@OrganizationId, @Slug, @Name, @CreatedBy)
RETURNING id, created_at;";

        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(sql, team, cancellationToken: cancellationToken);
        var row = await connection
            .QuerySingleAsync<(Guid Id, DateTimeOffset CreatedAt)>(command)
            .ConfigureAwait(false);
        team.Id = row.Id;
        team.CreatedAt = row.CreatedAt;
        return row.Id;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        Team team,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(team);
        // organization_id is immutable — moving a team between orgs is a separate
        // explicit operation (and would require ReBAC-aware reasoning). slug + name
        // are the only fields a team-rename mutates today.
        const string sql = @"
UPDATE teams
SET slug = @Slug,
    name = @Name
WHERE id = @Id;";

        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(sql, team, cancellationToken: cancellationToken);
        var rows = await connection.ExecuteAsync(command).ConfigureAwait(false);
        return rows > 0;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            "DELETE FROM teams WHERE id = @Id",
            new { Id = id },
            cancellationToken: cancellationToken
        );
        var rows = await connection.ExecuteAsync(command).ConfigureAwait(false);
        return rows > 0;
    }
}
