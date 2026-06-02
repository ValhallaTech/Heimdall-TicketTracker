/**
 * Unit spec for the login form action (`/login` +page.server.ts).
 *
 * Mocks `$env/dynamic/private` (origin), a fake `RequestEvent` (request/fetch/
 * cookies), and asserts the Set-Cookie forwarding, the safe-returnUrl redirect
 * (including the `//evil` open-redirect rejection), and the fail() branches for
 * MFA / invalid_grant / other. `redirect` throws, so it is asserted via rejects.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { RequestEvent } from '@sveltejs/kit';

import { env } from '$env/dynamic/private';
import { actions } from './+page.server';

vi.mock('$env/dynamic/private', () => ({ env: {} as Record<string, string | undefined> }));

interface RedirectError {
  status: number;
  location: string;
}

function isRedirect(value: unknown): value is RedirectError {
  return typeof value === 'object' && value !== null && 'status' in value && 'location' in value;
}

function buildEvent(opts: { fields: Record<string, string>; response: Response }): {
  event: RequestEvent;
  cookieSet: ReturnType<typeof vi.fn>;
  fetchMock: ReturnType<typeof vi.fn>;
} {
  const form = new FormData();
  for (const [k, v] of Object.entries(opts.fields)) {
    form.set(k, v);
  }
  const fetchMock = vi.fn(async () => opts.response);
  const cookieSet = vi.fn();
  const event = {
    request: { formData: async () => form },
    fetch: fetchMock,
    cookies: { set: cookieSet },
  } as unknown as RequestEvent;
  return { event, cookieSet, fetchMock };
}

async function run(event: RequestEvent): Promise<unknown> {
  const action = actions.default as (e: RequestEvent) => Promise<unknown>;
  return action(event);
}

beforeEach(() => {
  delete env.INTERNAL_API_ORIGIN;
});

afterEach(() => {
  vi.clearAllMocks();
});

describe('/login +page.server.ts default action', () => {
  it('POSTs JSON { email, password } to {origin}/api/v1/auth/token', async () => {
    const { event, fetchMock } = buildEvent({
      fields: { email: 'odin@asgard.test', password: 'secret' },
      response: new Response(null, { status: 200 }),
    });

    await expect(run(event)).rejects.toBeDefined(); // redirect throws on success

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('http://127.0.0.1:5000/api/v1/auth/token');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body as string)).toEqual({
      email: 'odin@asgard.test',
      password: 'secret',
    });
  });

  it('resolves the API origin from env.INTERNAL_API_ORIGIN', async () => {
    env.INTERNAL_API_ORIGIN = 'http://api.internal:6000';
    const { event, fetchMock } = buildEvent({
      fields: { email: 'a@b.test', password: 'p' },
      response: new Response(null, { status: 200 }),
    });

    await expect(run(event)).rejects.toBeDefined();

    expect((fetchMock.mock.calls[0] as [string])[0]).toBe(
      'http://api.internal:6000/api/v1/auth/token',
    );
  });

  it('forwards every API Set-Cookie onto the response via cookies.set on 200', async () => {
    const headers = new Headers();
    headers.append(
      'set-cookie',
      '__Host-heimdall_refresh=abc; Path=/; Max-Age=1209600; HttpOnly; Secure; SameSite=Strict',
    );
    const { event, cookieSet } = buildEvent({
      fields: { email: 'a@b.test', password: 'p' },
      response: new Response(null, { status: 200, headers }),
    });

    await expect(run(event)).rejects.toBeDefined();

    expect(cookieSet).toHaveBeenCalledWith(
      '__Host-heimdall_refresh',
      'abc',
      expect.objectContaining({
        path: '/',
        maxAge: 1209600,
        httpOnly: true,
        secure: true,
        sameSite: 'strict',
      }),
    );
  });

  it('redirects (303) to a safe returnUrl that starts with a single "/"', async () => {
    const { event } = buildEvent({
      fields: { email: 'a@b.test', password: 'p', returnUrl: '/tickets/5' },
      response: new Response(null, { status: 200 }),
    });

    try {
      await run(event);
      throw new Error('expected redirect');
    } catch (e) {
      expect(isRedirect(e)).toBe(true);
      if (isRedirect(e)) {
        expect(e.status).toBe(303);
        expect(e.location).toBe('/tickets/5');
      }
    }
  });

  it('redirects (303) to /tickets when returnUrl is absent', async () => {
    const { event } = buildEvent({
      fields: { email: 'a@b.test', password: 'p' },
      response: new Response(null, { status: 200 }),
    });

    try {
      await run(event);
      throw new Error('expected redirect');
    } catch (e) {
      expect(isRedirect(e) && e.location).toBe('/tickets');
    }
  });

  it('rejects an open-redirect returnUrl ("//evil") and falls back to /tickets', async () => {
    const { event } = buildEvent({
      fields: { email: 'a@b.test', password: 'p', returnUrl: '//evil.com' },
      response: new Response(null, { status: 200 }),
    });

    try {
      await run(event);
      throw new Error('expected redirect');
    } catch (e) {
      expect(isRedirect(e) && e.location).toBe('/tickets');
    }
  });

  it('returns fail(400, mfa-unavailable) on 401 requires_two_factor', async () => {
    const { event } = buildEvent({
      fields: { email: 'a@b.test', password: 'p' },
      response: new Response(JSON.stringify({ requires_two_factor: true }), { status: 401 }),
    });

    const result = (await run(event)) as { status: number; data: { error: string; email: string } };
    expect(result.status).toBe(400);
    expect(result.data).toEqual({ error: 'mfa-unavailable', email: 'a@b.test' });
  });

  it('returns fail(400, invalid-credentials) on 400 invalid_grant', async () => {
    const { event } = buildEvent({
      fields: { email: 'a@b.test', password: 'p' },
      response: new Response(JSON.stringify({ error: 'invalid_grant' }), { status: 400 }),
    });

    const result = (await run(event)) as { status: number; data: { error: string } };
    expect(result.status).toBe(400);
    expect(result.data.error).toBe('invalid-credentials');
  });

  it('returns fail(400, invalid-credentials) on any other non-OK response', async () => {
    const { event } = buildEvent({
      fields: { email: 'a@b.test', password: 'p' },
      response: new Response('boom', { status: 500 }),
    });

    const result = (await run(event)) as { status: number; data: { error: string } };
    expect(result.data.error).toBe('invalid-credentials');
  });

  it('never logs credentials', async () => {
    const spy = vi.spyOn(console, 'log').mockImplementation(() => {});
    const errSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const { event } = buildEvent({
      fields: { email: 'a@b.test', password: 'topsecret' },
      response: new Response(JSON.stringify({ error: 'invalid_grant' }), { status: 400 }),
    });

    await run(event);

    for (const call of [...spy.mock.calls, ...errSpy.mock.calls]) {
      expect(JSON.stringify(call)).not.toContain('topsecret');
    }
    spy.mockRestore();
    errSpy.mockRestore();
  });
});
