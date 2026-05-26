using System;
using System.Threading;
using System.Threading.Tasks;

namespace Heimdall.BLL.Tokens;

/// <summary>
/// Phase 5.5 step 11 — Redis-backed denylist for access-token JTIs.
/// </summary>
/// <remarks>
/// <para>
/// The bearer-validation pipeline (<see cref="Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents.OnTokenValidated"/>
/// wired in <c>Program.cs</c>) consults this seam on every authenticated request so that
/// an access token whose <c>jti</c> has been added by the logout / admin-revoke paths
/// can be refused before its natural <c>exp</c>. The keyspace is
/// <c>heimdall:token-denylist:&lt;jti&gt;</c>; the value is the revoke reason (one of
/// <c>logout</c>, <c>admin_revoke</c>, <c>family_replay</c>); the TTL is the remaining
/// access-token lifetime so the entry self-expires the moment the token would have
/// expired anyway, avoiding unbounded Redis growth.
/// </para>
/// </remarks>
public interface IAccessTokenDenylist
{
    /// <summary>
    /// Adds <paramref name="jti"/> to the denylist with a TTL derived from the remaining
    /// token lifetime. Implementations clamp already-expired (or nearly expired) tokens to
    /// a small positive minimum so the entry still survives validator clock skew; the Redis
    /// implementation uses a 30-second floor. Idempotent — a second call with a later expiry
    /// extends the TTL; a second call with an earlier or equal expiry is a no-op write that
    /// still succeeds.
    /// </summary>
    /// <param name="jti">The access token's <c>jti</c> claim. Must not be null or whitespace.</param>
    /// <param name="expiresAt">The token's <c>exp</c> as a UTC instant — drives the entry TTL.</param>
    /// <param name="reason">Audit-friendly revoke reason (<c>logout</c>, <c>admin_revoke</c>, <c>family_replay</c>).</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>A task representing the asynchronous write.</returns>
    Task DenyAsync(string jti, DateTimeOffset expiresAt, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the denylist status for <paramref name="jti"/>. <see cref="DenylistLookup.Denied"/>
    /// is <see langword="true"/> when an entry exists; <see cref="DenylistLookup.Reason"/>
    /// carries the recorded reason when populated. Implementations MUST distinguish a
    /// confirmed miss from a transport failure by throwing on the latter — the caller
    /// (<see cref="Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents.OnTokenValidated"/>
    /// wire-up) applies the Phase 5.5 step 12 fail-closed-for-admins / fail-open-for-reads
    /// outage policy and must be able to tell the two cases apart.
    /// </summary>
    /// <param name="jti">The access token's <c>jti</c> claim. Must not be null or whitespace.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>The lookup result.</returns>
    Task<DenylistLookup> IsDeniedAsync(string jti, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of an <see cref="IAccessTokenDenylist.IsDeniedAsync"/> call.
/// </summary>
/// <param name="Denied"><see langword="true"/> when an entry exists for the queried jti.</param>
/// <param name="Reason">The recorded revoke reason when <paramref name="Denied"/> is <see langword="true"/>; otherwise <see langword="null"/>.</param>
public readonly record struct DenylistLookup(bool Denied, string? Reason);
