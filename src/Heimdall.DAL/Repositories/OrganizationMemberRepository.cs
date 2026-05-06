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
/// Dapper implementation of <see cref="IOrganizationMemberRepository"/>. See
/// <see cref="OrganizationRepository"/> for the conventions this class follows
/// (Npgsql per call, <see cref="CommandDefinition"/> for cancellation,
/// <c>ConfigureAwait(false)</c> on every library-side await).
/// </summary>
/// <remarks>
/// The <c>role</c> column is the Postgres enum <c>org_member_role</c>; Npgsql 10
/// cannot materialise a custom enum directly to <see cref="string"/>, so the SELECT
/// projection casts <c>role::text AS Role</c> (analogous to the <c>citext::text</c>
/// pattern in <see cref="OrganizationRepository"/>). On the write path, INSERT and
/// UPDATE bind <c>@Role::org_member_role</c> so Postgres reinterprets the inbound
/// text as the enum type.
/// </remarks>
public sealed class OrganizationMemberRepository : IOrganizationMemberRepository
{
    private const string SelectColumns =
        "user_id AS UserId, organization_id AS OrganizationId, role::text AS Role, "
        + "added_at AS AddedAt, added_by AS AddedBy";

    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="OrganizationMemberRepository"/> class.
    /// </summary>
    /// <param name="options">Bound <see cref="DataOptions"/> providing the Postgres connection string.</param>
    public OrganizationMemberRepository(IOptions<DataOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = options.Value.PostgresConnectionString;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OrganizationMember>> GetByParentAsync(
        Guid parentId,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            $"SELECT {SelectColumns} FROM organization_members "
                + "WHERE organization_id = @OrganizationId "
                + "ORDER BY added_at ASC, user_id ASC",
            new { OrganizationId = parentId },
            cancellationToken: cancellationToken
        );
        var rows = await connection.QueryAsync<OrganizationMember>(command).ConfigureAwait(false);
        return [.. rows];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OrganizationMember>> GetByUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            $"SELECT {SelectColumns} FROM organization_members "
                + "WHERE user_id = @UserId "
                + "ORDER BY added_at ASC, organization_id ASC",
            new { UserId = userId },
            cancellationToken: cancellationToken
        );
        var rows = await connection.QueryAsync<OrganizationMember>(command).ConfigureAwait(false);
        return [.. rows];
    }

    /// <inheritdoc />
    public async Task<OrganizationMember?> GetAsync(
        Guid userId,
        Guid parentId,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            $"SELECT {SelectColumns} FROM organization_members "
                + "WHERE user_id = @UserId AND organization_id = @OrganizationId",
            new { UserId = userId, OrganizationId = parentId },
            cancellationToken: cancellationToken
        );
        return await connection
            .QuerySingleOrDefaultAsync<OrganizationMember>(command)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddAsync(
        OrganizationMember member,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(member);
        // added_at is sourced from the column DEFAULT now() and so is omitted from
        // the column list. The @Role::org_member_role cast is required because
        // Npgsql binds @Role as plain text and Postgres has no implicit cast from
        // text to a custom enum type.
        const string sql = @"
INSERT INTO organization_members (user_id, organization_id, role, added_by)
VALUES (@UserId, @OrganizationId, @Role::org_member_role, @AddedBy);";

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
UPDATE organization_members
SET role = @Role::org_member_role
WHERE user_id = @UserId AND organization_id = @OrganizationId;";

        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            sql,
            new
            {
                UserId = userId,
                OrganizationId = parentId,
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
            "DELETE FROM organization_members "
                + "WHERE user_id = @UserId AND organization_id = @OrganizationId",
            new { UserId = userId, OrganizationId = parentId },
            cancellationToken: cancellationToken
        );
        var rows = await connection.ExecuteAsync(command).ConfigureAwait(false);
        return rows > 0;
    }
}
