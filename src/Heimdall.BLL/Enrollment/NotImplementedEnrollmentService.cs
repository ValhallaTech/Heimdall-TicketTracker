using System;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Models;

namespace Heimdall.BLL.Enrollment;

/// <summary>
/// Default <see cref="IUserEnrollmentService"/> binding for Phase 2. Throws
/// <see cref="NotImplementedException"/> on every call so a premature consumer of the
/// seam fails loudly instead of silently fabricating a user or no-op'ing. Replaced in a
/// later phase by <c>AdminInviteEnrollmentService</c> (admin-driven invite flow) or
/// <c>LdapEnrollmentService</c> (directory-backed onboarding) per
/// <c>docs/proposals/team-collaboration.md</c> §8.
/// </summary>
public sealed class NotImplementedEnrollmentService : IUserEnrollmentService
{
    /// <inheritdoc />
    /// <exception cref="NotImplementedException">
    /// Always thrown. The Phase 2 seam is intentionally fail-loud; see
    /// <c>docs/proposals/team-collaboration.md</c> §8 for the planned implementations
    /// (<c>AdminInviteEnrollmentService</c>, <c>LdapEnrollmentService</c>).
    /// </exception>
    public Task<HeimdallUser> EnrollAsync(
        EnrollmentRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        throw new NotImplementedException(
            "User enrollment is a Phase-2 futureproofing seam (docs/proposals/team-collaboration.md §8) "
                + "and has no implementation yet. Replace the IUserEnrollmentService binding with "
                + "AdminInviteEnrollmentService or LdapEnrollmentService when the corresponding feature ships."
        );
    }
}
