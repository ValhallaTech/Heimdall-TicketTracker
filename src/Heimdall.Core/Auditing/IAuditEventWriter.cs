using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace Heimdall.Core.Auditing;

/// <summary>
/// Append-only writer for security and lifecycle events. The seam isolates application
/// code from the underlying persistence (a Dapper-backed implementation lives in
/// <c>Heimdall.DAL</c>) and lets callers — login endpoints, future MFA / token issuance
/// flows, admin tools — emit audit rows without taking a DAL dependency.
/// </summary>
public interface IAuditEventWriter
{
    /// <summary>
    /// Persists the supplied <paramref name="auditEvent"/> to the audit log.
    /// </summary>
    /// <param name="auditEvent">The event to record. Must not be <c>null</c>.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the write to complete.</param>
    /// <returns>A task representing the asynchronous write.</returns>
    Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the supplied <paramref name="auditEvent"/> on a caller-supplied
    /// <paramref name="connection"/> enlisted in the caller-supplied
    /// <paramref name="transaction"/>. Used when the audit row must commit (or rollback)
    /// atomically with another mutation — for example, the ticket route / assign flow
    /// in <c>docs/proposals/team-collaboration.md</c> §5.4 where the audit-event INSERT
    /// rides alongside the <c>tickets</c> UPDATE so a half-applied route is impossible.
    /// </summary>
    /// <remarks>
    /// Transactional contract: the caller owns the lifetime of <paramref name="connection"/>
    /// and <paramref name="transaction"/> — opening the connection, beginning the
    /// transaction, and committing or rolling back. This method does not open, commit,
    /// rollback, or dispose either resource; it only issues an <c>ExecuteAsync</c>
    /// against them.
    /// </remarks>
    /// <param name="connection">An already-open database connection. Must not be <c>null</c>.</param>
    /// <param name="transaction">An active transaction on <paramref name="connection"/>. Must not be <c>null</c>.</param>
    /// <param name="auditEvent">The event to record. Must not be <c>null</c>.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the write to complete.</param>
    /// <returns>A task representing the asynchronous write.</returns>
    Task WriteAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        AuditEvent auditEvent,
        CancellationToken cancellationToken = default);
}
