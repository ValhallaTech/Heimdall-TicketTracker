using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Heimdall.BLL.Authorization.OpenFga;

/// <summary>
/// A single OpenFGA tuple key. Mirrors <see cref="OpenFga.Sdk.Client.Model.ClientTupleKey"/>
/// but lives in the BLL surface so callers (services, the bootstrapper, the backfill
/// job) do not take a transitive dependency on the SDK type for ordinary tuple
/// shapes. See <see cref="TupleShapes"/> for the canonical helpers that produce
/// these values.
/// </summary>
/// <param name="User">Subject id, including the <c>user:</c> type prefix.</param>
/// <param name="Relation">Relation name as declared in <c>authz/model.fga</c>.</param>
/// <param name="Object">Object id, including the type prefix (e.g. <c>ticket:42</c>).</param>
public sealed record TupleKey(string User, string Relation, string Object);

/// <summary>
/// Append-only writer for OpenFGA tuples, per <c>docs/proposals/openfga.md</c> §3
/// step 7 (tuple-write hooks). Each call issues a single OpenFGA <c>Write</c>, so
/// writes + deletes commit atomically server-side per request.
/// </summary>
/// <remarks>
/// <para>
/// Direct-write contract (option (a) in <c>openfga.md</c> §3 step 7): the DB write
/// is the source of truth and has already committed by the time this writer is
/// called. On failure the writer logs a warning, records an
/// <c>openfga_tuple_write_failed</c> audit event, and <strong>swallows</strong> the
/// exception — the backfill job is the reconciliation safety net. Callers therefore
/// never need a try/catch around a tuple write to keep their own happy path correct.
/// </para>
/// <para>
/// Cancellation propagates as <see cref="System.OperationCanceledException"/> so
/// cooperative shutdown is honoured.
/// </para>
/// </remarks>
public interface ITupleWriter
{
    /// <summary>
    /// Writes the given tuple sets in a single OpenFGA <c>Write</c>. Either list may
    /// be empty (but not both). On failure: logs, audits, and swallows.
    /// </summary>
    /// <param name="writes">Tuples to add.</param>
    /// <param name="deletes">Tuples to remove.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    Task WriteAsync(
        IReadOnlyList<TupleKey> writes,
        IReadOnlyList<TupleKey> deletes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Convenience overload: writes a single tuple. Equivalent to
    /// <see cref="WriteAsync(IReadOnlyList{TupleKey}, IReadOnlyList{TupleKey}, CancellationToken)"/>
    /// with one write and no deletes.
    /// </summary>
    Task WriteAsync(TupleKey single, CancellationToken cancellationToken);

    /// <summary>
    /// Convenience overload: replaces one tuple with another atomically (one
    /// OpenFGA <c>Write</c> call). Either side may be <c>null</c> — pass
    /// <paramref name="delete"/> = <c>null</c> for an initial assign and
    /// <paramref name="write"/> = <c>null</c> for an unassign. Exactly the
    /// assignee-change pattern called out in <c>docs/proposals/openfga.md</c> §3
    /// step 7.
    /// </summary>
    Task ReplaceAsync(TupleKey? delete, TupleKey? write, CancellationToken cancellationToken);
}
