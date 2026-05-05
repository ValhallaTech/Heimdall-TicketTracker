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
/// Dapper implementation of <see cref="ITeamMemberRepository"/>. Mirrors
/// <see cref="OrganizationMemberRepository"/> in shape but works in
/// <see cref="TeamMemberRole"/> values rather than wire-string roles. The bridge
/// between the .NET enum and the Postgres <c>team_member_role</c> enum is
/// <see cref="TeamMemberRoleTypeHandler"/>, registered once in
/// <see cref="Heimdall.DAL.Extensions.ServiceCollectionExtensions.AddDal"/>; the
/// type handler emits and parses the snake_case wire strings, while the SQL still
/// applies <c>::team_member_role</c> on the way into the column because Npgsql
/// otherwise binds the parameter as plain text.
/// </summary>
public sealed class TeamMemberRepository : ITeamMemberRepository
{
    private const string SelectColumns =
        "user_id AS UserId, team_id AS TeamId, role::text AS Role, "
        + "added_at AS AddedAt, added_by AS AddedBy";

    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="TeamMemberRepository"/> class.
    /// </summary>
    /// <param name="options">Bound <see cref="DataOptions"/> providing the Postgres connection string.</param>
    public TeamMemberRepository(IOptions<DataOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = options.Value.PostgresConnectionString;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TeamMember>> GetByParentAsync(
        Guid parentId,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            $"SELECT {SelectColumns} FROM team_members "
                + "WHERE team_id = @TeamId "
                + "ORDER BY added_at ASC, user_id ASC",
            new { TeamId = parentId },
            cancellationToken: cancellationToken
        );
        var rows = await connection.QueryAsync<TeamMember>(command).ConfigureAwait(false);
        return [.. rows];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TeamMember>> GetByUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            $"SELECT {SelectColumns} FROM team_members "
                + "WHERE user_id = @UserId "
                + "ORDER BY added_at ASC, team_id ASC",
            new { UserId = userId },
            cancellationToken: cancellationToken
        );
        var rows = await connection.QueryAsync<TeamMember>(command).ConfigureAwait(false);
        return [.. rows];
    }

    /// <inheritdoc />
    public async Task<TeamMember?> GetAsync(
        Guid userId,
        Guid parentId,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            $"SELECT {SelectColumns} FROM team_members "
                + "WHERE user_id = @UserId AND team_id = @TeamId",
            new { UserId = userId, TeamId = parentId },
            cancellationToken: cancellationToken
        );
        return await connection
            .QuerySingleOrDefaultAsync<TeamMember>(command)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddAsync(TeamMember member, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        // The TeamMemberRoleTypeHandler binds @Role as the snake_case wire string;
        // the ::team_member_role cast then reinterprets it as the enum on Postgres'
        // side. added_at is sourced from the column DEFAULT now() and is omitted.
        const string sql = @"
INSERT INTO team_members (user_id, team_id, role, added_by)
VALUES (@UserId, @TeamId, @Role::team_member_role, @AddedBy);";

        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(sql, member, cancellationToken: cancellationToken);
        await connection.ExecuteAsync(command).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateRoleAsync(
        Guid userId,
        Guid parentId,
        TeamMemberRole role,
        CancellationToken cancellationToken = default
    )
    {
        const string sql = @"
UPDATE team_members
SET role = @Role::team_member_role
WHERE user_id = @UserId AND team_id = @TeamId;";

        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            sql,
            new
            {
                UserId = userId,
                TeamId = parentId,
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
            "DELETE FROM team_members WHERE user_id = @UserId AND team_id = @TeamId",
            new { UserId = userId, TeamId = parentId },
            cancellationToken: cancellationToken
        );
        var rows = await connection.ExecuteAsync(command).ConfigureAwait(false);
        return rows > 0;
    }
}
