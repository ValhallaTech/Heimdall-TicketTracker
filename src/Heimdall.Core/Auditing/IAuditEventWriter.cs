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
}
