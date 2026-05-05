using System;
using System.Collections.Generic;
using System.Data;
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
/// <see cref="TeamMemberRole"/> values rather than wire-string roles.
/// </summary>
/// <remarks>
/// Dapper 2.1.72 short-circuits custom <c>SqlMapper.TypeHandler&lt;TEnum&gt;</c>
/// registrations symmetrically: on writes it transmits the underlying integer,
/// and on reads it materializes via its built-in case-sensitive
/// <c>Enum.Parse</c>. This bypasses any handler hooks. To keep the wire format
/// (<c>team_lead</c>) decoupled from the .NET enum names (<c>TeamLead</c>) the
/// repository materializes rows into an internal <see cref="TeamMemberRow"/>
/// DTO whose <c>Role</c> is a plain <see cref="string"/>, and projects to the
/// domain via <see cref="TeamMemberRoleConverter.ParseWireString"/>. Writes
/// bind <c>@Role</c> explicitly as text via <see cref="DynamicParameters"/> and
/// rely on the <c>::team_member_role</c> cast in SQL.
/// </remarks>
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

    private static TeamMember ToDomain(TeamMemberRow row) => new()
    {
        UserId = row.UserId,
        TeamId = row.TeamId,
        Role = TeamMemberRoleConverter.ParseWireString(row.Role),
        AddedAt = row.AddedAt,
        AddedBy = row.AddedBy,
    };

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
        var rows = await connection.QueryAsync<TeamMemberRow>(command).ConfigureAwait(false);
        var result = new List<TeamMember>();
        foreach (var row in rows)
        {
            result.Add(ToDomain(row));
        }

        return result;
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
        var rows = await connection.QueryAsync<TeamMemberRow>(command).ConfigureAwait(false);
        var result = new List<TeamMember>();
        foreach (var row in rows)
        {
            result.Add(ToDomain(row));
        }

        return result;
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
        var row = await connection
            .QuerySingleOrDefaultAsync<TeamMemberRow>(command)
            .ConfigureAwait(false);
        return row is null ? null : ToDomain(row);
    }

    /// <inheritdoc />
    public async Task AddAsync(TeamMember member, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        // Dapper 2.1.72 short-circuits custom TypeHandler<TEnum> when the parameter
        // source is a strongly-typed enum *property* on an object and transmits the
        // underlying integer instead of invoking SetValue. Postgres then rejects the
        // ::team_member_role cast against an integer. To work around this we bind
        // @Role explicitly as text via DynamicParameters using the wire string, and
        // let the ::team_member_role cast in the SQL reinterpret it as the enum.
        // added_at is sourced from the column DEFAULT now() and is omitted.
        const string sql = @"
INSERT INTO team_members (user_id, team_id, role, added_by)
VALUES (@UserId, @TeamId, @Role::team_member_role, @AddedBy);";

        var parameters = new DynamicParameters();
        parameters.Add("UserId", member.UserId);
        parameters.Add("TeamId", member.TeamId);
        parameters.Add("Role", member.Role.ToWireString(), DbType.String);
        parameters.Add("AddedBy", member.AddedBy);

        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(sql, parameters, cancellationToken: cancellationToken);
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

        // See AddAsync for why @Role is bound as text via DynamicParameters rather
        // than relying on TeamMemberRoleTypeHandler for the write path.
        var parameters = new DynamicParameters();
        parameters.Add("UserId", userId);
        parameters.Add("TeamId", parentId);
        parameters.Add("Role", role.ToWireString(), DbType.String);

        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            sql,
            parameters,
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

/// <summary>
/// Internal Dapper materialization target for <c>team_members</c> rows. The
/// <see cref="Role"/> property is bound as a plain <see cref="string"/>
/// (the column is projected as <c>role::text AS Role</c>) so Dapper's
/// built-in enum-property short-circuit never engages; the row is then
/// projected to <see cref="TeamMember"/> via
/// <see cref="TeamMemberRoleConverter.ParseWireString"/>.
/// </summary>
internal sealed class TeamMemberRow
{
    public Guid UserId { get; set; }

    public Guid TeamId { get; set; }

    public string Role { get; set; } = string.Empty;

    public DateTimeOffset AddedAt { get; set; }

    public Guid AddedBy { get; set; }
}
