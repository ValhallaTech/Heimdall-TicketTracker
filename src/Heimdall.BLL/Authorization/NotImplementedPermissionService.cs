using System;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Models;

namespace Heimdall.BLL.Authorization;

/// <summary>
/// Placeholder <see cref="IPermissionService"/> implementation bound when the
/// <c>Authorization:Provider</c> configuration key is set to <c>"OpenFga"</c> ahead
/// of Phase 3. Every method throws <see cref="NotImplementedException"/> so a
/// premature flip of the flag fails loudly at the first authorization call rather
/// than silently allowing or denying actions.
/// </summary>
/// <remarks>
/// The real Phase 3 implementation (<c>OpenFgaPermissionService</c>) is delivered
/// by <c>docs/proposals/openfga.md</c> step 6 and will replace this class without
/// changing any call site.
/// </remarks>
public sealed class NotImplementedPermissionService : IPermissionService
{
    private const string Reason =
        "OpenFGA-backed permission service is reserved for Phase 3 (docs/proposals/openfga.md step 6).";

    /// <inheritdoc />
    public Task<bool> CanViewTeamQueueAsync(Guid actorId, Guid teamId, CancellationToken cancellationToken)
        => throw new NotImplementedException(Reason);

    /// <inheritdoc />
    public Task<bool> CanRouteTicketAsync(Guid actorId, Ticket ticket, Guid destinationTeamId, CancellationToken cancellationToken)
        => throw new NotImplementedException(Reason);

    /// <inheritdoc />
    public Task<bool> CanAssignTicketAsync(Guid actorId, Ticket ticket, Guid targetUserId, CancellationToken cancellationToken)
        => throw new NotImplementedException(Reason);

    /// <inheritdoc />
    public Task<bool> CanManageTeamMembersAsync(Guid actorId, Guid teamId, CancellationToken cancellationToken)
        => throw new NotImplementedException(Reason);
}
