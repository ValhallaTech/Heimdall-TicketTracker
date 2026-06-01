/**
 * Server hooks for Heimdall.Frontend — the auth glue that makes SvelteKit a
 * drop-in replacement for the retiring Blazor frontend.
 *
 * Per `blazor-to-svelte-transition.md` §4.2 (Topology B) and Phase 6.2 step 7,
 * the `handle` hook below:
 *
 *   1. Reads the Phase 5 `__Host-heimdall_refresh` refresh cookie.
 *   2. Exchanges it for a short-lived access token via the internal
 *      `POST /api/v1/auth/refresh` endpoint, forwarding the incoming refresh
 *      cookie and propagating any rotated `Set-Cookie` back to the browser so
 *      refresh-token rotation stays unbroken.
 *   3. Populates `event.locals.user`, `event.locals.accessToken`, and
 *      `event.locals.checkPermission()` for downstream loaders / actions.
 *
 * Security posture:
 *   - The access token is kept ONLY in server memory (`event.locals`); it is
 *     never written to a JS-readable cookie or sent to the browser.
 *   - `checkPermission` is deny-closed: any non-200, malformed body, or network
 *     error resolves to `false`, mirroring the backend so the UI fails safe.
 *   - Unauthenticated requests never throw — public pages still render.
 *
 * SvelteKit `handle` / `event.locals` / `App.Locals` typing was verified against
 * the official Svelte MCP docs (`kit/hooks`, `kit/types`) before authoring, as
 * mandated by the Phase 6 checklist.
 */
import { env } from '$env/dynamic/private';
import type { Handle } from '@sveltejs/kit';

/**
 * Phase 5 refresh-cookie name. The `__Host-` prefix binds the cookie to the
 * exact origin over HTTPS with `Path=/` and no `Domain`, so it can only be set
 * and read on the SvelteKit origin. Centralized here so the contract has a
 * single source of truth.
 */
export const REFRESH_COOKIE_NAME = '__Host-heimdall_refresh';

/** HTTP Authorization scheme used for the in-memory access token. */
const AUTH_SCHEME = 'Bearer';

/**
 * Internal origin of `Heimdall.Web`. Resolved from `$env/dynamic/private` so it
 * is configurable per environment and never bundled into client code. The dev
 * default targets the loopback interface per the single-container design (the
 * Node SSR process and the API share the host, so server-to-server traffic
 * stays on `127.0.0.1`).
 */
const DEFAULT_INTERNAL_API_ORIGIN = 'http://127.0.0.1:5000';

/** Resolve the API origin at call time so test/runtime env changes are honoured. */
function internalApiOrigin(): string {
  return env.INTERNAL_API_ORIGIN ?? DEFAULT_INTERNAL_API_ORIGIN;
}

/** Result of a refresh-cookie → access-token exchange. */
interface RefreshExchange {
  /** The bearer access token, or `null` when the exchange did not yield one. */
  accessToken: string | null;
  /**
   * Raw `Set-Cookie` headers returned by the API (rotated refresh cookie on
   * success, expiring cookie on 401). Propagated verbatim to the browser.
   */
  setCookies: string[];
}

/**
 * Exchange the refresh cookie for an access token via `POST /api/v1/auth/refresh`.
 *
 * The incoming refresh cookie is forwarded on the server-to-server request; the
 * API reads the token from it, responds 200 with the access token (and a
 * rotated `Set-Cookie`) or 401 (with an expiring `Set-Cookie`). Any rotated
 * cookies are returned for propagation. Network failures resolve to an empty,
 * unauthenticated result — the hook must never throw.
 */
async function exchangeRefreshForAccess(
  fetchFn: typeof fetch,
  refreshToken: string,
): Promise<RefreshExchange> {
  const url = `${internalApiOrigin()}/api/v1/auth/refresh`;
  try {
    const response = await fetchFn(url, {
      method: 'POST',
      // Forward the refresh token as a cookie so the API's cookie-driven
      // refresh path reads it exactly as it would from a browser request.
      headers: { cookie: `${REFRESH_COOKIE_NAME}=${refreshToken}` },
    });

    const setCookies = response.headers.getSetCookie();

    if (!response.ok) {
      // 401 — invalid / expired / replayed token. Still propagate the expiring
      // Set-Cookie so a stale refresh cookie is cleared from the browser.
      return { accessToken: null, setCookies };
    }

    const body: unknown = await response.json();
    return { accessToken: extractAccessToken(body), setCookies };
  } catch {
    // Network / transport error: fail unauthenticated, leave cookies untouched.
    return { accessToken: null, setCookies: [] };
  }
}

/** Narrow the refresh-response body to its `access_token` string. */
function extractAccessToken(body: unknown): string | null {
  if (typeof body !== 'object' || body === null) {
    return null;
  }
  const token = (body as Record<string, unknown>).access_token;
  return typeof token === 'string' && token.length > 0 ? token : null;
}

/**
 * Derive a minimal {@link App.User} from the access-token JWT payload.
 *
 * The signature is deliberately NOT verified here: `Heimdall.Web` already
 * verified it when it issued the token in exchange for the (server-forwarded)
 * refresh cookie, so re-verifying in the SSR process would add a JWKS/crypto
 * dependency for zero security gain. We only base64url-decode the payload to
 * read identity claims; the token is never trusted for authorization decisions
 * (those go back through the API via {@link createPermissionChecker}).
 */
function decodeUserFromAccessToken(accessToken: string): App.User | null {
  const parts = accessToken.split('.');
  if (parts.length !== 3) {
    return null;
  }
  try {
    const payloadJson = Buffer.from(parts[1], 'base64url').toString('utf8');
    const payload: unknown = JSON.parse(payloadJson);
    return userFromClaims(payload);
  } catch {
    return null;
  }
}

/** Build an {@link App.User} from decoded JWT claims, requiring a `sub`. */
function userFromClaims(payload: unknown): App.User | null {
  if (typeof payload !== 'object' || payload === null) {
    return null;
  }
  const claims = payload as Record<string, unknown>;

  const sub = claims.sub;
  if (typeof sub !== 'string' || sub.length === 0) {
    return null;
  }

  const user: App.User = { id: sub };

  if (typeof claims.email === 'string') {
    user.email = claims.email;
  }

  const name = claims.name ?? claims.preferred_username ?? claims.unique_name;
  if (typeof name === 'string') {
    user.name = name;
  }

  return user;
}

/**
 * Build the server-side OpenFGA permission checker bound to a bearer token.
 *
 * Calls `POST /api/v1/authz/check` with an `Authorization` bearer header and a
 * `{ relation, object }` body; the endpoint derives the subject from the token
 * server-side so a caller cannot probe as another user. Deny-closed: any
 * non-200 response, malformed body, or network error resolves to `false`.
 */
function createPermissionChecker(
  fetchFn: typeof fetch,
  accessToken: string,
): (relation: string, object: string) => Promise<boolean> {
  return async (relation: string, object: string): Promise<boolean> => {
    const url = `${internalApiOrigin()}/api/v1/authz/check`;
    try {
      const response = await fetchFn(url, {
        method: 'POST',
        headers: {
          authorization: `${AUTH_SCHEME} ${accessToken}`,
          'content-type': 'application/json',
        },
        body: JSON.stringify({ relation, object }),
      });

      if (!response.ok) {
        return false;
      }

      const body: unknown = await response.json();
      return extractAllowed(body);
    } catch {
      return false;
    }
  };
}

/** Narrow the authz-check response body to its `allowed` boolean (deny-closed). */
function extractAllowed(body: unknown): boolean {
  if (typeof body !== 'object' || body === null) {
    return false;
  }
  return (body as Record<string, unknown>).allowed === true;
}

export const handle: Handle = async ({ event, resolve }) => {
  // Start deny-closed: unauthenticated until the refresh exchange proves otherwise.
  event.locals.user = null;
  event.locals.accessToken = null;
  event.locals.checkPermission = async () => false;

  // Phase 6.2 step 7 security-review (Medium) mitigation: the refresh exchange
  // ROTATES the Phase 5 `__Host-heimdall_refresh` token, and re-presenting an
  // already-rotated token trips family-replay detection (force-logout). Only
  // run it for genuine app routes/endpoints:
  //   - `event.route.id === null` → static assets (`/_app/immutable/*`,
  //     favicon, unmatched paths) which never need auth locals.
  //   - `event.isSubRequest` → server-side `fetch` from a load function to the
  //     app's own endpoints; it reuses the parent render's context, so a second
  //     rotation within one render must be avoided.
  // Skipping these leaves the deny-closed defaults above in place.
  //
  // Residual limitation (tracked as a Phase 6 follow-up, intentionally not
  // fixed here): truly-concurrent browser-initiated requests to multiple
  // endpoints during one CSR navigation can still race the rotation. A fuller
  // fix (single-flight refresh / access-token cache / API rotation grace
  // window) is out of scope for this step.
  if (event.route.id === null || event.isSubRequest) {
    return resolve(event);
  }

  const refreshToken = event.cookies.get(REFRESH_COOKIE_NAME);
  const rotatedCookies: string[] = [];

  if (refreshToken) {
    const exchange = await exchangeRefreshForAccess(event.fetch, refreshToken);
    rotatedCookies.push(...exchange.setCookies);

    if (exchange.accessToken) {
      event.locals.accessToken = exchange.accessToken;
      event.locals.user = decodeUserFromAccessToken(exchange.accessToken);
      event.locals.checkPermission = createPermissionChecker(event.fetch, exchange.accessToken);
    }
  }

  const response = await resolve(event);

  // Propagate the API's rotated / expiring refresh cookie verbatim so
  // refresh-token rotation (and stale-cookie clearing on 401) stays unbroken.
  for (const cookie of rotatedCookies) {
    response.headers.append('set-cookie', cookie);
  }

  return response;
};
