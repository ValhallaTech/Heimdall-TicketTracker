using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Heimdall.Core.Auditing;
using Heimdall.DAL.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Heimdall.DAL.Auditing;

/// <summary>
/// Dapper-backed <see cref="IAuditEventReader"/>. Mirrors the conventions used by
/// <see cref="AuditEventWriter"/> (single-call <see cref="NpgsqlConnection"/>,
/// <see cref="CommandDefinition"/> for cancellation, <c>ConfigureAwait(false)</c>)
/// and projects the <c>inet</c> / <c>jsonb</c> columns through explicit text casts
/// so Dapper materializes them into plain <see cref="string"/> properties.
/// </summary>
public sealed class AuditEventReader : IAuditEventReader
{
    /// <summary>
    /// Defensive ceiling on a single read. The admin feed renders rows into the
    /// browser DOM; an unbounded fetch would balloon both server memory and
    /// client render time. 1000 matches the worst-case page the read-side UI is
    /// designed for.
    /// </summary>
    private const int MaxLimit = 1000;

    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditEventReader"/> class.
    /// </summary>
    /// <param name="options">Bound <see cref="DataOptions"/> providing the Postgres connection string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
    public AuditEventReader(IOptions<DataOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = options.Value.PostgresConnectionString;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditEventRecord>> GetRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return Array.Empty<AuditEventRecord>();
        }

        int effectiveLimit = limit > MaxLimit ? MaxLimit : limit;

        // The ix_audit_events_occurred_at (occurred_at DESC) index from
        // M202605050002 directly serves this ORDER BY + LIMIT path.
        const string sql = @"
SELECT
    id            AS Id,
    occurred_at   AS OccurredAt,
    actor_user_id AS ActorUserId,
    event_type    AS EventType,
    target        AS Target,
    ip::text      AS Ip,
    user_agent    AS UserAgent,
    payload::text AS PayloadJson
FROM   audit_events
ORDER  BY occurred_at DESC
LIMIT  @Limit;";

        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            sql,
            new { Limit = effectiveLimit },
            cancellationToken: cancellationToken);
        var rows = await connection
            .QueryAsync<AuditEventRecord>(command)
            .ConfigureAwait(false);
        return [.. rows];
    }
}
