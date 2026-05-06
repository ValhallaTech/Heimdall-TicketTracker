using System;
using System.Collections.Generic;
using Heimdall.Core.Models;

namespace Heimdall.BLL.Enrollment;

/// <summary>
/// Inbound request describing a user that should be enrolled into the application.
/// Captured as a futureproofing seam per <c>docs/proposals/team-collaboration.md</c> §8;
/// not consumed by any caller in Phase 2. Future implementations
/// (<c>AdminInviteEnrollmentService</c>, <c>LdapEnrollmentService</c>) will materialize
/// a <see cref="HeimdallUser"/> from this request and optionally seed an initial team
/// membership.
/// </summary>
/// <param name="Email">
/// The candidate user's email address. Implementations are expected to normalize and
/// uniqueness-check this value before persisting.
/// </param>
/// <param name="DisplayName">The user-facing display name shown in the UI and audit events.</param>
/// <param name="DefaultTeamId">
/// Optional team to seed an initial membership in. <see langword="null"/> when the
/// caller does not want to attach the new user to any team yet (e.g. admin invites
/// that defer team placement until first login).
/// </param>
/// <param name="DefaultTeamRole">
/// Optional role used for the seeded membership; ignored when <paramref name="DefaultTeamId"/>
/// is <see langword="null"/>. <see langword="null"/> here means "implementation default"
/// (typically <see cref="TeamMemberRole.Member"/>).
/// </param>
/// <param name="Attributes">
/// Free-form provider-supplied claims (e.g. LDAP attributes, SSO claim values) carried
/// through for downstream auditing or mapping. Empty when the caller has no extra
/// metadata. Keys are case-sensitive by contract.
/// </param>
public sealed record EnrollmentRequest(
    string Email,
    string DisplayName,
    Guid? DefaultTeamId,
    TeamMemberRole? DefaultTeamRole,
    IReadOnlyDictionary<string, string> Attributes
);
