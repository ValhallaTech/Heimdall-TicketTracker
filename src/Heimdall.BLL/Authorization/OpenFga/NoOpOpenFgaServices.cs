using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Heimdall.BLL.Authorization.OpenFga;

/// <summary>
/// Null-object <see cref="IOpenFgaAuthorizationService"/> registered when no
/// OpenFGA sidecar is configured (typical dev / unit-test setup).
/// </summary>
/// <remarks>
/// Returns deny-closed for all check operations and an empty list for
/// <see cref="ListObjectsAsync"/> — matching the failure contract of the real
/// adapter so callers cannot tell whether a sidecar is present, only that
/// nothing is allowed. This keeps the deny-closed invariant intact for
/// non-configured environments.
/// </remarks>
internal sealed class NoOpOpenFgaAuthorizationService : IOpenFgaAuthorizationService
{
    private readonly ILogger<NoOpOpenFgaAuthorizationService> _logger;

    public NoOpOpenFgaAuthorizationService(ILogger<NoOpOpenFgaAuthorizationService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public Task<bool> CheckAsync(FgaCheckRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _logger.LogDebug("OpenFGA not configured; CheckAsync returning deny-closed for {User} {Relation} {Object}.",
            request.User, request.Relation, request.Object);
        return Task.FromResult(false);
    }

    public Task<IReadOnlyList<bool>> BatchCheckAsync(
        IReadOnlyList<FgaCheckRequest> requests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        bool[] results = new bool[requests.Count];
        return Task.FromResult<IReadOnlyList<bool>>(results);
    }

    public Task<IReadOnlyList<string>> ListObjectsAsync(
        FgaListObjectsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    public Task<IReadOnlyList<string>> ListUsersAsync(
        FgaListUsersRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _logger.LogDebug(
            "OpenFGA not configured; ListUsersAsync returning empty for {ObjectType}:{ObjectId}#{Relation}.",
            request.ObjectType, request.ObjectId, request.Relation);
        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    public Task<FgaExpandResult> ExpandAsync(
        FgaExpandRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _logger.LogDebug(
            "OpenFGA not configured; ExpandAsync returning empty tree for {ObjectType}:{ObjectId}#{Relation}.",
            request.ObjectType, request.ObjectId, request.Relation);
        return Task.FromResult(new FgaExpandResult(null));
    }
}

/// <summary>
/// Null-object <see cref="ITupleWriter"/> registered when no OpenFGA sidecar
/// is configured. All write operations are no-ops; this matches the
/// "log + audit + swallow" failure contract of the real writer (minus the
/// audit row, which would be noisy in dev).
/// </summary>
internal sealed class NoOpTupleWriter : ITupleWriter
{
    public Task WriteAsync(TupleKey single, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(single);
        return Task.CompletedTask;
    }

    public Task WriteAsync(
        IReadOnlyList<TupleKey> writes,
        IReadOnlyList<TupleKey> deletes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writes);
        ArgumentNullException.ThrowIfNull(deletes);
        return Task.CompletedTask;
    }

    public Task ReplaceAsync(
        TupleKey? delete,
        TupleKey? write,
        CancellationToken cancellationToken) => Task.CompletedTask;
}
