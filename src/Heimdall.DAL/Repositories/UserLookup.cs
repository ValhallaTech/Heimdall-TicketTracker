using System;
using System.Collections.Generic;
using System.Linq;
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
    public async Task<bool> IsTwoFactorEnabledAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            "SELECT two_factor_enabled FROM users WHERE id = @Id",
            new { Id = userId },
            cancellationToken: cancellationToken
        );
        // Same deny-closed pattern as IsSystemAdminAsync: a missing row maps
        // to false, never an exception. The Phase 4.6 step 16 handler relies
        // on this contract (treat unknown users as MFA-not-enabled).
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
        // email is a citext column; Npgsql 10 + Dapper cannot materialise citext
        // directly to string, so cast to text in the projection (writes still
        // work because text->citext is implicit on input). See HeimdallUserStore.
        var command = new CommandDefinition(
            "SELECT id AS Id, email::text AS Email FROM users WHERE id = @Id",
            new { Id = userId },
            cancellationToken: cancellationToken
        );
        return await connection
            .QuerySingleOrDefaultAsync<UserSummary>(command)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserSummary>> SearchByEmailAsync(
        string emailFragment,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emailFragment);

        // Empty / whitespace-only fragments must NOT degenerate into a full
        // table scan — the admin UI debounces but a stuck key combo could
        // still trigger one, so we short-circuit explicitly.
        if (string.IsNullOrWhiteSpace(emailFragment))
        {
            return Array.Empty<UserSummary>();
        }

        // Clamp to a sane upper bound so a malicious / buggy caller cannot
        // request the entire users table. Phase 3.6 picker shows ≤ 25 rows.
        const int MaxLimit = 100;
        int effectiveLimit = limit <= 0 ? 25 : Math.Min(limit, MaxLimit);

        await using var connection = new NpgsqlConnection(_connectionString);
        // ILIKE for case-insensitive substring; LIKE-pattern metacharacters in
        // the user input are escaped explicitly so a fragment of "%" cannot
        // match every row. The `\` escape character is the Postgres default;
        // we're explicit for readability.
        string escaped = emailFragment
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

        var command = new CommandDefinition(
            // email is a citext column; cast to text in the projection so
            // Npgsql/Dapper can materialise it to string (see HeimdallUserStore).
            @"SELECT id AS Id, email::text AS Email
              FROM users
              WHERE email ILIKE @Pattern ESCAPE '\'
              ORDER BY email ASC
              LIMIT @Limit",
            new { Pattern = "%" + escaped + "%", Limit = effectiveLimit },
            cancellationToken: cancellationToken
        );

        IEnumerable<UserSummary> rows = await connection
            .QueryAsync<UserSummary>(command)
            .ConfigureAwait(false);
        return rows.ToList();
    }
}
