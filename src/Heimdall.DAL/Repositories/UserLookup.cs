using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Heimdall.Core.Interfaces;
using Heimdall.DAL.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Heimdall.DAL.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IUserLookup"/>. Issues a single targeted
/// <c>SELECT system_admin FROM users WHERE id = …</c> against the existing
/// <c>users</c> table created by <c>M202605050001</c>.
/// </summary>
/// <remarks>
/// Deliberately disjoint from <c>HeimdallUserStore</c> so authorization code does
/// not have to take a dependency on <c>Microsoft.AspNetCore.Identity</c> just to
/// read a single boolean.
/// </remarks>
public sealed class UserLookup : IUserLookup
{
    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserLookup"/> class.
    /// </summary>
    /// <param name="options">Bound <see cref="DataOptions"/> providing the Postgres connection string.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="options"/> is <c>null</c>.</exception>
    public UserLookup(IOptions<DataOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = options.Value.PostgresConnectionString;
    }

    /// <inheritdoc />
    public async Task<bool> IsSystemAdminAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            "SELECT system_admin FROM users WHERE id = @Id",
            new { Id = userId },
            cancellationToken: cancellationToken
        );
        // QuerySingleOrDefaultAsync<bool> would fold a missing row to false (default);
        // be explicit so the deny-closed semantics are easy to read.
        var flag = await connection
            .QuerySingleOrDefaultAsync<bool?>(command)
            .ConfigureAwait(false);
        return flag == true;
    }

    /// <inheritdoc />
    public async Task<UserSummary?> GetByIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            "SELECT id AS Id, email AS Email FROM users WHERE id = @Id",
            new { Id = userId },
            cancellationToken: cancellationToken
        );
        return await connection
            .QuerySingleOrDefaultAsync<UserSummary>(command)
            .ConfigureAwait(false);
    }
}
