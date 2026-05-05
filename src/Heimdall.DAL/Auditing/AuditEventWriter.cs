using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Heimdall.Core.Auditing;
using Heimdall.DAL.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Heimdall.DAL.Auditing;

/// <summary>
/// Dapper-backed <see cref="IAuditEventWriter"/>. Mirrors <see cref="Identity.HeimdallUserStore"/>
/// and <see cref="Repositories.TicketRepository"/>: an <see cref="NpgsqlConnection"/> is opened
/// per call, the SQL goes through a <see cref="CommandDefinition"/> so cancellation flows end-to-end,
/// and library-side awaits use <c>ConfigureAwait(false)</c>.
/// </summary>
public sealed class AuditEventWriter : IAuditEventWriter
{
    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditEventWriter"/> class.
    /// </summary>
    /// <param name="options">Bound <see cref="DataOptions"/> providing the Postgres connection string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
    public AuditEventWriter(IOptions<DataOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = options.Value.PostgresConnectionString;
    }

    /// <inheritdoc />
    public async Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        cancellationToken.ThrowIfCancellationRequested();

        // occurred_at is intentionally omitted — the column default (now()) sources the
        // timestamp from the database clock, matching HeimdallUserStore. The explicit
        // ::inet / ::jsonb casts let Dapper bind the parameters as plain strings while
        // still landing in the strongly-typed Postgres columns.
        const string sql = @"
INSERT INTO audit_events
    (actor_user_id, event_type, target, ip, user_agent, payload)
VALUES
    (@ActorUserId, @EventType, @Target, @Ip::inet, @UserAgent, @PayloadJson::jsonb);";

        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(sql, auditEvent, cancellationToken: cancellationToken);
        await connection.ExecuteAsync(command).ConfigureAwait(false);
    }
}
