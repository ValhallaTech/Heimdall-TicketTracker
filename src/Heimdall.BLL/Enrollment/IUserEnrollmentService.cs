using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Models;

namespace Heimdall.BLL.Enrollment;

/// <summary>
/// Futureproofing seam for user enrollment per
/// <c>docs/proposals/team-collaboration.md</c> §8. <b>Not consumed by any caller in
/// Phase 2.</b> The interface is captured now so the eventual implementations
/// (<c>AdminInviteEnrollmentService</c> for the admin-driven invite flow and
/// <c>LdapEnrollmentService</c> for directory-backed onboarding) drop into the same
/// shape without a churn-y refactor of every call site.
/// </summary>
/// <remarks>
/// Bound in <c>ApplicationModule</c> to <see cref="NotImplementedEnrollmentService"/>:
/// any production call before the real implementation ships will throw a
/// <see cref="System.NotImplementedException"/> immediately, surfacing a premature
/// integration loudly instead of silently no-op'ing or fabricating a user.
/// </remarks>
public interface IUserEnrollmentService
{
    /// <summary>
    /// Materializes a <see cref="HeimdallUser"/> for the supplied
    /// <paramref name="request"/> and (optionally) seeds an initial team membership per
    /// the <c>DefaultTeamId</c> / <c>DefaultTeamRole</c> on the request. Implementations
    /// are responsible for uniqueness checks, password / credential bootstrapping (if
    /// any), and emitting an <c>audit_events</c> row for the enrollment.
    /// </summary>
    /// <param name="request">The enrollment request. Must not be <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>The newly-enrolled user.</returns>
    Task<HeimdallUser> EnrollAsync(
        EnrollmentRequest request,
        CancellationToken cancellationToken = default
    );
}
