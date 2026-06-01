/**
 * Unit spec for `hooks.server.ts` (Phase 6.2 step 7 — SvelteKit auth glue).
 *
 * These tests exercise the `handle` hook end-to-end against a fake
 * `RequestEvent`: the global `fetch` is mocked (no real network), the
 * `__Host-heimdall_refresh` cookie presence is stubbed, and `resolve` returns a
 * real `Response` whose `Set-Cookie` headers we can assert on.
 *
 * Behaviour under test (see `hooks.server.ts` doc-comment):
 *   - Refresh-cookie → access-token exchange via `POST /api/v1/auth/refresh`.
 *   - `event.locals.user` decoded (no signature verify) from the JWT payload.
 *   - `event.locals.checkPermission` deny-closed wrapper over
 *     `POST /api/v1/authz/check`.
 *   - Rotated / expiring `Set-Cookie` propagation back onto the response.
 *   - `env.INTERNAL_API_ORIGIN` override.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Handle, RequestEvent } from '@sveltejs/kit';

import { env } from '$env/dynamic/private';

import { REFRESH_COOKIE_NAME, handle } from './hooks.server';

// `$env/dynamic/private` is virtual at runtime; provide a mutable stub so each
// test can toggle `INTERNAL_API_ORIGIN` and assert the resolved origin.
vi.mock('$env/dynamic/private', () => ({
  env: {} as Record<string, string | undefined>,
}));

const DEFAULT_ORIGIN = 'http://127.0.0.1:5000';
const REFRESH_URL = `${DEFAULT_ORIGIN}/api/v1/auth/refresh`;
const AUTHZ_URL = `${DEFAULT_ORIGIN}/api/v1/authz/check`;

/** Base64url-encode a UTF-8 string (matches the SUT's `Buffer.from` decode). */
function base64url(input: string): string {
  return Buffer.from(input, 'utf8').toString('base64url');
}

/**
 * Build a minimal *unsigned* JWT (`header.payload.signature`). The signature
 * segment is an opaque placeholder — the hook decodes the payload only and never
 * verifies it, so any non-empty third segment is sufficient.
 */
function makeJwt(claims: Record<string, unknown>): string {
  const header = base64url(JSON.stringify({ alg: 'none', typ: 'JWT' }));
  const payload = base64url(JSON.stringify(claims));
  return `${header}.${payload}.unsigned`;
}

/** Construct a JSON `Response` with optional `Set-Cookie` headers. */
function jsonResponse(status: number, body: unknown, setCookies: readonly string[] = []): Response {
  const headers = new Headers();
  for (const cookie of setCookies) {
    headers.append('set-cookie', cookie);
  }
  headers.set('content-type', 'application/json');
  return new Response(JSON.stringify(body), { status, headers });
}

interface RouteHandlers {
  /** Handler for `POST /api/v1/auth/refresh`. */
  refresh?: () => Promise<Response>;
  /** Handler for `POST /api/v1/authz/check`. */
  authz?: () => Promise<Response>;
}

type FetchMock = ReturnType<typeof vi.fn>;

/**
 * Build a `fetch` mock that routes by pathname. Returns the raw `vi.fn` so call
 * arguments (URL / init) can be asserted; cast to `typeof fetch` at the call
 * site where SvelteKit's `event.fetch` type is required.
 */
function createFetchMock(handlers: RouteHandlers): FetchMock {
  return vi.fn(async (input: RequestInfo | URL): Promise<Response> => {
    const url = typeof input === 'string' ? input : input.toString();
    if (url.endsWith('/api/v1/auth/refresh')) {
      if (!handlers.refresh) {
        throw new Error(`unexpected refresh call: ${url}`);
      }
      return handlers.refresh();
    }
    if (url.endsWith('/api/v1/authz/check')) {
      if (!handlers.authz) {
        throw new Error(`unexpected authz call: ${url}`);
      }
      return handlers.authz();
    }
    throw new Error(`unexpected fetch url: ${url}`);
  });
}

interface BuiltEvent {
  event: RequestEvent;
  resolve: ReturnType<typeof vi.fn>;
  cookieGet: ReturnType<typeof vi.fn>;
}

/** Assemble a minimal fake `RequestEvent` plus a `resolve` spy. */
function buildEvent(options: {
  refreshToken?: string;
  fetch: FetchMock;
  resolveResponse?: Response;
}): BuiltEvent {
  const cookieGet = vi.fn((name: string): string | undefined =>
    name === REFRESH_COOKIE_NAME ? options.refreshToken : undefined,
  );

  const locals = {} as App.Locals;
  const event = {
    locals,
    cookies: { get: cookieGet, set: vi.fn(), delete: vi.fn() },
    fetch: options.fetch as unknown as typeof fetch,
    request: new Request('http://localhost/'),
    url: new URL('http://localhost/'),
  } as unknown as RequestEvent;

  const resolve = vi.fn(
    async (): Promise<Response> => options.resolveResponse ?? new Response('ok'),
  );

  return { event, resolve, cookieGet };
}

/** Invoke the hook with a built event, returning the outgoing response. */
async function runHandle(built: BuiltEvent): Promise<Response> {
  const run = handle as Handle;
  return run({
    event: built.event,
    resolve: built.resolve as unknown as Parameters<Handle>[0]['resolve'],
  }) as Promise<Response>;
}

/** Find the init object the fetch mock was called with for a given pathname. */
function initForUrl(fetchMock: FetchMock, suffix: string): RequestInit | undefined {
  const call = fetchMock.mock.calls.find(([input]) =>
    (typeof input === 'string' ? input : String(input)).endsWith(suffix),
  );
  return call?.[1] as RequestInit | undefined;
}

beforeEach(() => {
  delete env.INTERNAL_API_ORIGIN;
});

afterEach(() => {
  vi.clearAllMocks();
});

describe('hooks.server — handle (refresh-cookie → access-token exchange)', () => {
  describe('authenticated locals on a successful exchange', () => {
    it('reads the refresh cookie and forwards it to /api/v1/auth/refresh', async () => {
      const token = makeJwt({ sub: 'user-1' });
      const fetchMock = createFetchMock({
        refresh: async () => jsonResponse(200, { access_token: token, token_type: 'Bearer' }),
      });
      const built = buildEvent({ refreshToken: 'refresh-abc', fetch: fetchMock });

      await runHandle(built);

      expect(built.cookieGet).toHaveBeenCalledWith(REFRESH_COOKIE_NAME);
      expect(fetchMock).toHaveBeenCalledWith(
        REFRESH_URL,
        expect.objectContaining({ method: 'POST' }),
      );
      const init = initForUrl(fetchMock, '/api/v1/auth/refresh');
      expect(init?.headers).toMatchObject({
        cookie: `${REFRESH_COOKIE_NAME}=refresh-abc`,
      });
    });

    it('populates locals.accessToken with the bearer string from the 200 body', async () => {
      const token = makeJwt({ sub: 'user-1' });
      const fetchMock = createFetchMock({
        refresh: async () => jsonResponse(200, { access_token: token, expires_in: 900 }),
      });
      const built = buildEvent({ refreshToken: 'refresh-abc', fetch: fetchMock });

      await runHandle(built);

      expect(built.event.locals.accessToken).toBe(token);
    });

    it('decodes the JWT payload (no signature verification) into locals.user with id = sub', async () => {
      const token = makeJwt({ sub: 'subject-42' });
      const fetchMock = createFetchMock({
        refresh: async () => jsonResponse(200, { access_token: token }),
      });
      const built = buildEvent({ refreshToken: 'refresh-abc', fetch: fetchMock });

      await runHandle(built);

      expect(built.event.locals.user).toEqual({ id: 'subject-42' });
    });

    it('maps email and name claims onto locals.user', async () => {
      const token = makeJwt({ sub: 'user-1', email: 'odin@asgard.test', name: 'Odin' });
      const fetchMock = createFetchMock({
        refresh: async () => jsonResponse(200, { access_token: token }),
      });
      const built = buildEvent({ refreshToken: 'refresh-abc', fetch: fetchMock });

      await runHandle(built);

      expect(built.event.locals.user).toEqual({
        id: 'user-1',
        email: 'odin@asgard.test',
        name: 'Odin',
      });
    });

    it('falls back to preferred_username then unique_name for the name claim', async () => {
      const preferred = makeJwt({ sub: 'u', preferred_username: 'heimdall' });
      const unique = makeJwt({ sub: 'u', unique_name: 'bifrost' });

      const builtPreferred = buildEvent({
        refreshToken: 'r',
        fetch: createFetchMock({
          refresh: async () => jsonResponse(200, { access_token: preferred }),
        }),
      });
      await runHandle(builtPreferred);
      expect(builtPreferred.event.locals.user?.name).toBe('heimdall');

      const builtUnique = buildEvent({
        refreshToken: 'r',
        fetch: createFetchMock({
          refresh: async () => jsonResponse(200, { access_token: unique }),
        }),
      });
      await runHandle(builtUnique);
      expect(builtUnique.event.locals.user?.name).toBe('bifrost');
    });

    it('keeps the access token server-side only — never written as a cookie', async () => {
      const token = makeJwt({ sub: 'user-1' });
      const fetchMock = createFetchMock({
        refresh: async () => jsonResponse(200, { access_token: token }),
      });
      const built = buildEvent({ refreshToken: 'refresh-abc', fetch: fetchMock });

      const response = await runHandle(built);

      const cookies = built.event.cookies as unknown as { set: ReturnType<typeof vi.fn> };
      expect(cookies.set).not.toHaveBeenCalled();
      const outgoing = response.headers.getSetCookie().join(';');
      expect(outgoing).not.toContain(token);
    });

    it('sets accessToken but leaves user null when the JWT payload has no sub', async () => {
      const token = makeJwt({ email: 'no-sub@asgard.test' });
      const fetchMock = createFetchMock({
        refresh: async () => jsonResponse(200, { access_token: token }),
      });
      const built = buildEvent({ refreshToken: 'refresh-abc', fetch: fetchMock });

      await runHandle(built);

      expect(built.event.locals.accessToken).toBe(token);
      expect(built.event.locals.user).toBeNull();
    });

    it('leaves user null when the access token is not a well-formed JWT', async () => {
      const fetchMock = createFetchMock({
        refresh: async () => jsonResponse(200, { access_token: 'not-a-jwt' }),
      });
      const built = buildEvent({ refreshToken: 'refresh-abc', fetch: fetchMock });

      await runHandle(built);

      expect(built.event.locals.accessToken).toBe('not-a-jwt');
      expect(built.event.locals.user).toBeNull();
    });

    it('treats a 200 body without a usable access_token as unauthenticated', async () => {
      const fetchMock = createFetchMock({
        refresh: async () => jsonResponse(200, { token_type: 'Bearer' }),
      });
      const built = buildEvent({ refreshToken: 'refresh-abc', fetch: fetchMock });

      await runHandle(built);

      expect(built.event.locals.accessToken).toBeNull();
      expect(built.event.locals.user).toBeNull();
    });
  });

  describe('checkPermission true / false', () => {
    it('posts { relation, object } with an Authorization bearer header to /api/v1/authz/check', async () => {
      const token = makeJwt({ sub: 'user-1' });
      const fetchMock = createFetchMock({
        refresh: async () => jsonResponse(200, { access_token: token }),
        authz: async () => jsonResponse(200, { allowed: true }),
      });
      const built = buildEvent({ refreshToken: 'refresh-abc', fetch: fetchMock });

      await runHandle(built);
      const allowed = await built.event.locals.checkPermission('viewer', 'ticket:1');

      expect(allowed).toBe(true);
      expect(fetchMock).toHaveBeenCalledWith(
        AUTHZ_URL,
        expect.objectContaining({ method: 'POST' }),
      );
      const init = initForUrl(fetchMock, '/api/v1/authz/check');
      expect(init?.headers).toMatchObject({
        authorization: ['Bearer', token].join(' '),
        'content-type': 'application/json',
      });
      expect(init?.body).toBe(JSON.stringify({ relation: 'viewer', object: 'ticket:1' }));
    });

    it('resolves true when the authz-check body is { allowed: true }', async () => {
      const token = makeJwt({ sub: 'user-1' });
      const built = buildEvent({
        refreshToken: 'r',
        fetch: createFetchMock({
          refresh: async () => jsonResponse(200, { access_token: token }),
          authz: async () => jsonResponse(200, { allowed: true }),
        }),
      });

      await runHandle(built);

      await expect(built.event.locals.checkPermission('viewer', 'ticket:1')).resolves.toBe(true);
    });

    it('resolves false when the authz-check body is { allowed: false }', async () => {
      const token = makeJwt({ sub: 'user-1' });
      const built = buildEvent({
        refreshToken: 'r',
        fetch: createFetchMock({
          refresh: async () => jsonResponse(200, { access_token: token }),
          authz: async () => jsonResponse(200, { allowed: false }),
        }),
      });

      await runHandle(built);

      await expect(built.event.locals.checkPermission('viewer', 'ticket:1')).resolves.toBe(false);
    });
  });

  describe('deny-closed posture', () => {
    it('resolves false on a non-200 authz-check response', async () => {
      const token = makeJwt({ sub: 'user-1' });
      const built = buildEvent({
        refreshToken: 'r',
        fetch: createFetchMock({
          refresh: async () => jsonResponse(200, { access_token: token }),
          authz: async () => jsonResponse(403, { allowed: true }),
        }),
      });

      await runHandle(built);

      await expect(built.event.locals.checkPermission('viewer', 'ticket:1')).resolves.toBe(false);
    });

    it('resolves false on a network / transport error', async () => {
      const token = makeJwt({ sub: 'user-1' });
      const built = buildEvent({
        refreshToken: 'r',
        fetch: createFetchMock({
          refresh: async () => jsonResponse(200, { access_token: token }),
          authz: async () => {
            throw new Error('ECONNREFUSED');
          },
        }),
      });

      await runHandle(built);

      await expect(built.event.locals.checkPermission('viewer', 'ticket:1')).resolves.toBe(false);
    });

    it('resolves false on a malformed / non-object authz-check body', async () => {
      const token = makeJwt({ sub: 'user-1' });
      const built = buildEvent({
        refreshToken: 'r',
        fetch: createFetchMock({
          refresh: async () => jsonResponse(200, { access_token: token }),
          authz: async () => jsonResponse(200, 'not-an-object'),
        }),
      });

      await runHandle(built);

      await expect(built.event.locals.checkPermission('viewer', 'ticket:1')).resolves.toBe(false);
    });

    it('resolves false when the authz-check body is not valid JSON', async () => {
      const token = makeJwt({ sub: 'user-1' });
      const built = buildEvent({
        refreshToken: 'r',
        fetch: createFetchMock({
          refresh: async () => jsonResponse(200, { access_token: token }),
          authz: async () => new Response('}{ not json', { status: 200 }),
        }),
      });

      await runHandle(built);

      await expect(built.event.locals.checkPermission('viewer', 'ticket:1')).resolves.toBe(false);
    });
  });

  describe('unauthenticated browsing', () => {
    it('leaves locals.user and accessToken null when no refresh cookie is present', async () => {
      const fetchMock = createFetchMock({});
      const built = buildEvent({ fetch: fetchMock });

      await runHandle(built);

      expect(built.event.locals.user).toBeNull();
      expect(built.event.locals.accessToken).toBeNull();
      expect(fetchMock).not.toHaveBeenCalled();
    });

    it('does not throw and still calls resolve when unauthenticated', async () => {
      const fetchMock = createFetchMock({});
      const built = buildEvent({ fetch: fetchMock });

      await expect(runHandle(built)).resolves.toBeInstanceOf(Response);
      expect(built.resolve).toHaveBeenCalledTimes(1);
    });

    it('default checkPermission resolves false before any successful exchange', async () => {
      const built = buildEvent({ fetch: createFetchMock({}) });

      await runHandle(built);

      await expect(built.event.locals.checkPermission('viewer', 'ticket:1')).resolves.toBe(false);
    });
  });

  describe('refresh 401 → unauthenticated', () => {
    it('treats a 401 from /api/v1/auth/refresh as unauthenticated without throwing', async () => {
      const fetchMock = createFetchMock({
        refresh: async () => jsonResponse(401, { error: 'invalid_grant' }),
      });
      const built = buildEvent({ refreshToken: 'stale', fetch: fetchMock });

      await expect(runHandle(built)).resolves.toBeInstanceOf(Response);
      expect(built.event.locals.user).toBeNull();
      expect(built.event.locals.accessToken).toBeNull();
      await expect(built.event.locals.checkPermission('viewer', 'ticket:1')).resolves.toBe(false);
    });

    it('does not throw and stays unauthenticated on a refresh network error', async () => {
      const fetchMock = createFetchMock({
        refresh: async () => {
          throw new Error('ECONNREFUSED');
        },
      });
      const built = buildEvent({ refreshToken: 'stale', fetch: fetchMock });

      await expect(runHandle(built)).resolves.toBeInstanceOf(Response);
      expect(built.event.locals.user).toBeNull();
      expect(built.event.locals.accessToken).toBeNull();
    });
  });

  describe('Set-Cookie propagation', () => {
    it('propagates the rotated refresh Set-Cookie from a 200 onto the response', async () => {
      const token = makeJwt({ sub: 'user-1' });
      const rotated = `${REFRESH_COOKIE_NAME}=rotated-value; Path=/; HttpOnly; Secure`;
      const fetchMock = createFetchMock({
        refresh: async () => jsonResponse(200, { access_token: token }, [rotated]),
      });
      const built = buildEvent({ refreshToken: 'refresh-abc', fetch: fetchMock });

      const response = await runHandle(built);

      expect(response.headers.getSetCookie()).toContain(rotated);
    });

    it('propagates the expiring Set-Cookie from a 401 so a stale cookie is cleared', async () => {
      const expiring = `${REFRESH_COOKIE_NAME}=; Path=/; Max-Age=0`;
      const fetchMock = createFetchMock({
        refresh: async () => jsonResponse(401, { error: 'invalid_grant' }, [expiring]),
      });
      const built = buildEvent({ refreshToken: 'stale', fetch: fetchMock });

      const response = await runHandle(built);

      expect(response.headers.getSetCookie()).toContain(expiring);
    });

    it('appends every Set-Cookie returned by the API without dropping any', async () => {
      const token = makeJwt({ sub: 'user-1' });
      const first = `${REFRESH_COOKIE_NAME}=rotated; Path=/`;
      const second = 'heimdall_session=s1; Path=/';
      const fetchMock = createFetchMock({
        refresh: async () => jsonResponse(200, { access_token: token }, [first, second]),
      });
      const built = buildEvent({ refreshToken: 'refresh-abc', fetch: fetchMock });

      const response = await runHandle(built);

      const cookies = response.headers.getSetCookie();
      expect(cookies).toContain(first);
      expect(cookies).toContain(second);
    });
  });

  describe('configuration', () => {
    it('targets env.INTERNAL_API_ORIGIN when set', async () => {
      env.INTERNAL_API_ORIGIN = 'http://api.internal:8080';
      const token = makeJwt({ sub: 'user-1' });
      const fetchMock = createFetchMock({
        refresh: async () => jsonResponse(200, { access_token: token }),
        authz: async () => jsonResponse(200, { allowed: true }),
      });
      const built = buildEvent({ refreshToken: 'refresh-abc', fetch: fetchMock });

      await runHandle(built);
      await built.event.locals.checkPermission('viewer', 'ticket:1');

      expect(fetchMock).toHaveBeenCalledWith(
        'http://api.internal:8080/api/v1/auth/refresh',
        expect.anything(),
      );
      expect(fetchMock).toHaveBeenCalledWith(
        'http://api.internal:8080/api/v1/authz/check',
        expect.anything(),
      );
    });

    it('falls back to the loopback dev default when INTERNAL_API_ORIGIN is unset', async () => {
      const token = makeJwt({ sub: 'user-1' });
      const fetchMock = createFetchMock({
        refresh: async () => jsonResponse(200, { access_token: token }),
      });
      const built = buildEvent({ refreshToken: 'refresh-abc', fetch: fetchMock });

      await runHandle(built);

      expect(fetchMock).toHaveBeenCalledWith(REFRESH_URL, expect.anything());
    });
  });
});
