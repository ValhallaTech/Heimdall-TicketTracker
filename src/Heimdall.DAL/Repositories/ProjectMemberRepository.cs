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
/// Dapper implementation of <see cref="IProjectMemberRepository"/>. Mirrors
/// <see cref="OrganizationMemberRepository"/> exactly — see the rationale there for
/// the <c>role::text AS Role</c> SELECT projection and the
/// <c>@Role::project_member_role</c> cast on writes.
/// </summary>
public sealed class ProjectMemberRepository : IProjectMemberRepository
{
    private const string SelectColumns =
        "user_id AS UserId, project_id AS ProjectId, role::text AS Role, "
        + "added_at AS AddedAt, added_by AS AddedBy";

    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectMemberRepository"/> class.
    /// </summary>
    /// <param name="options">Bound <see cref="DataOptions"/> providing the Postgres connection string.</param>
    public ProjectMemberRepository(IOptions<DataOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = options.Value.PostgresConnectionString;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProjectMember>> GetByParentAsync(
        Guid parentId,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            $"SELECT {SelectColumns} FROM project_members "
                + "WHERE project_id = @ProjectId "
                + "ORDER BY added_at ASC, user_id ASC",
            new { ProjectId = parentId },
            cancellationToken: cancellationToken
        );
        var rows = await connection.QueryAsync<ProjectMember>(command).ConfigureAwait(false);
        return [.. rows];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProjectMember>> GetByUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            $"SELECT {SelectColumns} FROM project_members "
                + "WHERE user_id = @UserId "
                + "ORDER BY added_at ASC, project_id ASC",
            new { UserId = userId },
            cancellationToken: cancellationToken
        );
        var rows = await connection.QueryAsync<ProjectMember>(command).ConfigureAwait(false);
        return [.. rows];
    }

    /// <inheritdoc />
    public async Task<ProjectMember?> GetAsync(
        Guid userId,
        Guid parentId,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            $"SELECT {SelectColumns} FROM project_members "
                + "WHERE user_id = @UserId AND project_id = @ProjectId",
            new { UserId = userId, ProjectId = parentId },
            cancellationToken: cancellationToken
        );
        return await connection
            .QuerySingleOrDefaultAsync<ProjectMember>(command)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddAsync(
        ProjectMember member,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(member);
        const string sql = @"
INSERT INTO project_members (user_id, project_id, role, added_by)
VALUES (@UserId, @ProjectId, @Role::project_member_role, @AddedBy);";

        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(sql, member, cancellationToken: cancellationToken);
        await connection.ExecuteAsync(command).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateRoleAsync(
        Guid userId,
        Guid parentId,
        string role,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        const string sql = @"
UPDATE project_members
SET role = @Role::project_member_role
WHERE user_id = @UserId AND project_id = @ProjectId;";

        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            sql,
            new
            {
                UserId = userId,
                ProjectId = parentId,
                Role = role,
            },
            cancellationToken: cancellationToken
        );
        var rows = await connection.ExecuteAsync(command).ConfigureAwait(false);
        return rows > 0;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(
        Guid userId,
        Guid parentId,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            "DELETE FROM project_members "
                + "WHERE user_id = @UserId AND project_id = @ProjectId",
            new { UserId = userId, ProjectId = parentId },
            cancellationToken: cancellationToken
        );
        var rows = await connection.ExecuteAsync(command).ConfigureAwait(false);
        return rows > 0;
    }
}
