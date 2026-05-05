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
/// Dapper implementation of <see cref="IProjectRepository"/>. See
/// <see cref="OrganizationRepository"/> for the conventions this class follows.
/// </summary>
public sealed class ProjectRepository : IProjectRepository
{
    private const string SelectColumns =
        "id AS Id, team_id AS TeamId, slug::text AS Slug, name AS Name, "
        + "created_at AS CreatedAt, created_by AS CreatedBy";

    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectRepository"/> class.
    /// </summary>
    /// <param name="options">Bound <see cref="DataOptions"/> providing the Postgres connection string.</param>
    public ProjectRepository(IOptions<DataOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = options.Value.PostgresConnectionString;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Project>> GetByTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            $"SELECT {SelectColumns} FROM projects WHERE team_id = @TeamId ORDER BY slug ASC",
            new { TeamId = teamId },
            cancellationToken: cancellationToken
        );
        var rows = await connection.QueryAsync<Project>(command).ConfigureAwait(false);
        return [.. rows];
    }

    /// <inheritdoc />
    public async Task<Project?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            $"SELECT {SelectColumns} FROM projects WHERE id = @Id",
            new { Id = id },
            cancellationToken: cancellationToken
        );
        return await connection.QuerySingleOrDefaultAsync<Project>(command).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Project?> GetBySlugAsync(
        Guid teamId,
        string slug,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        await using var connection = new NpgsqlConnection(_connectionString);
        // ::citext cast — see OrganizationRepository.GetBySlugAsync for rationale.
        var command = new CommandDefinition(
            $"SELECT {SelectColumns} FROM projects WHERE team_id = @TeamId AND slug = @Slug::citext",
            new { TeamId = teamId, Slug = slug },
            cancellationToken: cancellationToken
        );
        return await connection.QuerySingleOrDefaultAsync<Project>(command).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Guid> CreateAsync(
        Project project,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(project);
        const string sql = @"
INSERT INTO projects (team_id, slug, name, created_by)
VALUES (@TeamId, @Slug, @Name, @CreatedBy)
RETURNING id, created_at;";

        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(sql, project, cancellationToken: cancellationToken);
        var row = await connection
            .QuerySingleAsync<(Guid Id, DateTimeOffset CreatedAt)>(command)
            .ConfigureAwait(false);
        project.Id = row.Id;
        project.CreatedAt = row.CreatedAt;
        return row.Id;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        Project project,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(project);
        // team_id is immutable — moving a project between teams is a separate
        // explicit operation (mirrors TeamRepository.UpdateAsync's stance on
        // organization_id).
        const string sql = @"
UPDATE projects
SET slug = @Slug,
    name = @Name
WHERE id = @Id;";

        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(sql, project, cancellationToken: cancellationToken);
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
            "DELETE FROM projects WHERE id = @Id",
            new { Id = id },
            cancellationToken: cancellationToken
        );
        var rows = await connection.ExecuteAsync(command).ConfigureAwait(false);
        return rows > 0;
    }
}
