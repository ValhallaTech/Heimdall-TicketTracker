using Microsoft.AspNetCore.Authorization;

namespace Heimdall.Web.Authorization.Policies;

/// <summary>
/// Authorization requirement representing "the acting user has satisfied the
/// Heimdall MFA gate" — i.e. is either not a privileged actor subject to MFA at
/// all, or is a privileged actor whose current session is authenticated with a
/// second factor and whose <c>users.two_factor_enabled</c> column is <c>true</c>.
/// </summary>
/// <remarks>
/// <para>
/// Served by <see cref="RequireMfaAuthorizationHandler"/> (Phase 4.6 step 16),
/// which consults OpenFGA to determine whether the actor is an admin of the
/// seed organization, inspects the <c>amr</c> claim for <c>mfa</c>, and reads
/// the live <c>users.two_factor_enabled</c> flag from the database. Non-admins
/// satisfy the requirement unconditionally; admins must satisfy all three
/// invariants. Break-glass parity with <see cref="OpenFgaAuthorizationHandler"/>
/// applies — see that handler's remarks for the contract.
/// </para>
/// </remarks>
public sealed class RequireMfaRequirement : IAuthorizationRequirement
{
}
