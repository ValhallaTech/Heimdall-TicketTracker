using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Heimdall.Core.Auditing;

/// <summary>
/// Read-side abstraction for the append-only <c>audit_events</c> table. Distinct
/// from <see cref="IAuditEventWriter"/> so write-side callers don't accidentally
/// take a dependency on read paths and vice-versa. Powers the admin audit feed
/// (Phase 2.8 step 25 of <c>docs/proposals/team-collaboration.md</c> §7).
/// </summary>
public interface IAuditEventReader
{
    /// <summary>
    /// Returns the most recent <paramref name="limit"/> audit-event rows ordered
    /// newest-first by <c>occurred_at</c>. Implementations must clamp
    /// <paramref name="limit"/> to a sane upper bound to avoid runaway result-set
    /// materialization.
    /// </summary>
    /// <param name="limit">Maximum number of rows to return. Must be &gt; 0.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    Task<IReadOnlyList<AuditEventRecord>> GetRecentAsync(
        int limit,
        CancellationToken cancellationToken = default);
}
