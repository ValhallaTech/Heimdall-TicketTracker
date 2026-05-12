using System;
using System.Collections.Generic;

namespace Heimdall.Web.Identity;

/// <summary>
/// One-shot in-memory store for freshly-generated MFA recovery codes so the
/// step-10 verify endpoint can stash them, redirect to the display page, and
/// have the display page consume-and-clear them in a single read.
/// </summary>
/// <remarks>
/// <para>
/// Phase 4.3 step 10 (<c>docs/implementation/phase-4-checklist.md</c>).
/// Recovery codes are <strong>never</strong> persisted on the server in
/// reversible form (the database only stores PBKDF2 hashes — see Phase 4.2
/// step 6) and <strong>never</strong> written to logs. This cache exists
/// strictly to carry the plaintext codes across the
/// <c>POST /account/mfa/setup/verify → 302 → GET /account/mfa/recovery-codes</c>
/// round-trip; entries expire after five minutes and are removed atomically
/// on first read.
/// </para>
/// <para>
/// Heimdall does not use MVC TempData and the cookie-backed alternative
/// (storing the codes in an authenticated session cookie) would round-trip
/// the plaintext codes through the browser, defeating the "shown once" model.
/// An in-memory cache keyed on <c>(user id, opaque token)</c> avoids both.
/// </para>
/// </remarks>
public interface IRecoveryCodeDisplayCache
{
    /// <summary>
    /// Stashes <paramref name="codes"/> against the supplied <paramref name="userId"/>
    /// and returns an opaque token the caller embeds in the redirect URL. Each
    /// call mints a fresh token; concurrent stashes do not collide.
    /// </summary>
    /// <param name="userId">The owning user. Part of the cache key so a stolen
    /// token from a different session cannot replay another user's codes.</param>
    /// <param name="codes">The plaintext recovery codes to stash. The collection
    /// is copied defensively; the caller may free its own reference immediately.</param>
    /// <returns>The opaque single-use token.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="codes"/> is <c>null</c>.
    /// </exception>
    Guid Stash(Guid userId, IReadOnlyList<string> codes);

    /// <summary>
    /// Atomically reads-and-removes the codes previously stashed against
    /// (<paramref name="userId"/>, <paramref name="token"/>). Returns
    /// <c>null</c> if no matching entry exists, the entry has expired, or the
    /// entry has already been consumed.
    /// </summary>
    /// <param name="userId">The owning user.</param>
    /// <param name="token">The opaque token returned by <see cref="Stash"/>.</param>
    /// <returns>The plaintext recovery codes, or <c>null</c> if not found.</returns>
    IReadOnlyList<string>? Consume(Guid userId, Guid token);
}
