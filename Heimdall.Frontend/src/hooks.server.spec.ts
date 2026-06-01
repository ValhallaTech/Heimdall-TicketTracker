// STUB: to be authored by JS/TS Unit Test Engineer
/**
 * STUB spec for `hooks.server.ts` (Phase 6.2 step 7 — auth glue).
 *
 * Ownership boundary: per `docs/implementation/phase-6-checklist.md` and the
 * repo's stored test-ownership convention, ALL real Vitest assertions for this
 * file are authored by the JavaScript/TypeScript Unit Test Engineer agent. The
 * Frontend Expert only scaffolds this stub enumerating the cases to cover.
 *
 * Do NOT add real assertions here — replace each `it.todo(...)` with a real
 * implementation when the test engineer takes ownership.
 *
 * Suggested mocking surface for the implementer:
 *   - `$env/dynamic/private` → stub `INTERNAL_API_ORIGIN`.
 *   - `event.fetch` → fake the `/api/v1/auth/refresh` and `/api/v1/authz/check`
 *     responses (200 / 401 / network throw, including `getSetCookie()`).
 *   - `event.cookies.get` → presence / absence of `__Host-heimdall_refresh`.
 *   - `resolve` → return a `Response` whose `Set-Cookie` headers can be asserted.
 */
import { describe, it } from 'vitest';

describe('hooks.server — handle (refresh-cookie → access-token exchange)', () => {
  // --- Authenticated locals shape on success -------------------------------
  it.todo(
    'reads __Host-heimdall_refresh, exchanges it, and forwards the cookie to /api/v1/auth/refresh',
  );
  it.todo('populates locals.accessToken with the bearer string from the 200 response body');
  it.todo('decodes the JWT payload (no signature verification) into locals.user with id = sub');
  it.todo('maps email / name (name|preferred_username|unique_name) claims onto locals.user');
  it.todo('keeps the access token server-side only — never written as a JS-readable cookie');

  // --- checkPermission true / false ----------------------------------------
  it.todo(
    'checkPermission posts { relation, object } with Authorization: ****** to /api/v1/authz/check',
  );
  it.todo('checkPermission resolves true when the authz-check body is { allowed: true }');
  it.todo('checkPermission resolves false when the authz-check body is { allowed: false }');

  // --- Deny-closed posture --------------------------------------------------
  it.todo('checkPermission resolves false on a non-200 authz-check response (e.g. 400 / 401)');
  it.todo('checkPermission resolves false on a network/transport error (deny-closed)');
  it.todo('checkPermission resolves false on a malformed / non-object authz-check body');

  // --- Unauthenticated browsing --------------------------------------------
  it.todo('leaves locals.user null and accessToken null when no refresh cookie is present');
  it.todo('does not throw when unauthenticated — public pages still render');
  it.todo('default locals.checkPermission resolves false before any successful exchange');

  // --- Refresh 401 → unauthenticated ---------------------------------------
  it.todo('treats a 401 from /api/v1/auth/refresh (invalid/expired/replayed) as unauthenticated');
  it.todo('does not populate locals.user / accessToken when the refresh exchange returns 401');

  // --- Cookie rotation propagation -----------------------------------------
  it.todo('propagates the rotated __Host-heimdall_refresh Set-Cookie from a 200 onto the response');
  it.todo('propagates the expiring Set-Cookie from a 401 so a stale refresh cookie is cleared');
  it.todo('appends every Set-Cookie returned by the API (getSetCookie) without dropping any');

  // --- Config ---------------------------------------------------------------
  it.todo('targets env.INTERNAL_API_ORIGIN when set, falling back to the loopback dev default');
});
