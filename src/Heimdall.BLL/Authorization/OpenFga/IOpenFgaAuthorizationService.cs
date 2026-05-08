using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Heimdall.BLL.Authorization.OpenFga;

/// <summary>
/// Authorization seam for OpenFGA <c>Check</c> / <c>BatchCheck</c> / <c>ListObjects</c>
/// calls, per <c>docs/proposals/openfga.md</c> §3 step 6.
/// </summary>
/// <remarks>
/// <para>
/// The interface is named with the <c>OpenFga</c> prefix to disambiguate from
/// <see cref="Microsoft.AspNetCore.Authorization.IAuthorizationService"/>, which has a
/// completely different shape and lives behind ASP.NET's policy pipeline.
/// </para>
/// <para>
/// Implementations <strong>must</strong> deny-closed on transport / 5xx failures
/// (return <c>false</c> from <see cref="CheckAsync"/>, all-<c>false</c> from
/// <see cref="BatchCheckAsync"/>, empty list from <see cref="ListObjectsAsync"/>) so
/// an unreachable sidecar never silently allows access. <see cref="System.OperationCanceledException"/>
/// must propagate so cooperative cancellation is honoured.
/// </para>
/// </remarks>
public interface IOpenFgaAuthorizationService
{
    /// <summary>
    /// Issues a single OpenFGA <c>Check</c>. Returns <c>false</c> on transport / 5xx
    /// failures (deny-closed).
    /// </summary>
    /// <param name="request">The check request.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    Task<bool> CheckAsync(FgaCheckRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Issues a batch of OpenFGA <c>Check</c> calls in a single round trip. Preferred
    /// for hot paths (queue pages) over multiple <see cref="CheckAsync"/> calls.
    /// Returns one <c>bool</c> per input request, in input order. On batch-level
    /// failure all entries are <c>false</c> (deny-closed).
    /// </summary>
    /// <param name="requests">The check requests, in deterministic order.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    Task<IReadOnlyList<bool>> BatchCheckAsync(
        IReadOnlyList<FgaCheckRequest> requests,
        CancellationToken cancellationToken);

    /// <summary>
    /// Issues an OpenFGA <c>ListObjects</c> call. Returns the list of object ids
    /// (fully qualified, e.g. <c>ticket:42</c>) the subject has the requested
    /// relation on. On failure returns an empty list (deny-closed).
    /// </summary>
    /// <param name="request">The list-objects request.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    Task<IReadOnlyList<string>> ListObjectsAsync(
        FgaListObjectsRequest request,
        CancellationToken cancellationToken);
}
