/**
 * Unit spec for the reset-password form action
 * (`/account/reset-password` +page.server.ts).
 *
 * Mocks `$env/dynamic/private` (origin) and a fake `RequestEvent`. Asserts the
 * `{ email, token, password, confirmPassword }` JSON payload forwarded to
 * `POST /api/v1/auth/reset-password`, the 200 → redirect(303, '/login?reset=success'),
 * the 400 `password_mismatch` / `invalid_token` (and unknown-shape fallback)
 * mappings (with email/token echoed back), and the 404 → `reset_unavailable`
 * branch. `redirect` throws, so success is asserted via a catch. Mirrors
 * `login/page.server.test.ts`.
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
  fetchMock: ReturnType<typeof vi.fn>;
} {
  const form = new FormData();
  for (const [k, v] of Object.entries(opts.fields)) {
    form.set(k, v);
  }
  const fetchMock = vi.fn(async () => opts.response);
  const event = {
    request: { formData: async () => form },
    fetch: fetchMock,
  } as unknown as RequestEvent;
  return { event, fetchMock };
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

describe('/account/reset-password +page.server.ts default action', () => {
  it('POSTs JSON { email, token, password, confirmPassword } to {origin}/api/v1/auth/reset-password', async () => {
    const { event, fetchMock } = buildEvent({
      fields: {
        email: 'odin@asgard.test',
        token: 'reset-token-123',
        password: 'newsecret',
        confirmPassword: 'newsecret',
      },
      response: new Response(JSON.stringify({ status: 'ok' }), { status: 200 }),
    });

    await expect(run(event)).rejects.toBeDefined(); // redirect throws on success

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('http://127.0.0.1:5000/api/v1/auth/reset-password');
    expect(init.method).toBe('POST');
    expect(init.headers).toEqual({ 'content-type': 'application/json' });
    expect(JSON.parse(init.body as string)).toEqual({
      email: 'odin@asgard.test',
      token: 'reset-token-123',
      password: 'newsecret',
      confirmPassword: 'newsecret',
    });
  });

  it('resolves the API origin from env.INTERNAL_API_ORIGIN', async () => {
    env.INTERNAL_API_ORIGIN = 'http://api.internal:6000';
    const { event, fetchMock } = buildEvent({
      fields: { email: 'a@b.test', token: 't', password: 'p', confirmPassword: 'p' },
      response: new Response(JSON.stringify({ status: 'ok' }), { status: 200 }),
    });

    await expect(run(event)).rejects.toBeDefined();

    expect((fetchMock.mock.calls[0] as [string])[0]).toBe(
      'http://api.internal:6000/api/v1/auth/reset-password',
    );
  });

  it('redirects (303) to /login?reset=success on success', async () => {
    const { event } = buildEvent({
      fields: { email: 'a@b.test', token: 't', password: 'p', confirmPassword: 'p' },
      response: new Response(JSON.stringify({ status: 'ok' }), { status: 200 }),
    });

    try {
      await run(event);
      throw new Error('expected redirect');
    } catch (e) {
      expect(isRedirect(e)).toBe(true);
      if (isRedirect(e)) {
        expect(e.status).toBe(303);
        expect(e.location).toBe('/login?reset=success');
      }
    }
  });

  it('returns fail(400, password_mismatch) echoing email and token on 400 password_mismatch', async () => {
    const { event } = buildEvent({
      fields: { email: 'a@b.test', token: 'tok-1', password: 'p', confirmPassword: 'q' },
      response: new Response(JSON.stringify({ error: 'password_mismatch' }), { status: 400 }),
    });

    const result = (await run(event)) as {
      status: number;
      data: { error: string; email: string; token: string };
    };
    expect(result.status).toBe(400);
    expect(result.data).toEqual({
      error: 'password_mismatch',
      email: 'a@b.test',
      token: 'tok-1',
    });
  });

  it('returns fail(400, invalid_token) echoing email and token on 400 invalid_token', async () => {
    const { event } = buildEvent({
      fields: { email: 'a@b.test', token: 'expired-tok', password: 'p', confirmPassword: 'p' },
      response: new Response(JSON.stringify({ error: 'invalid_token' }), { status: 400 }),
    });

    const result = (await run(event)) as {
      status: number;
      data: { error: string; email: string; token: string };
    };
    expect(result.status).toBe(400);
    expect(result.data).toEqual({
      error: 'invalid_token',
      email: 'a@b.test',
      token: 'expired-tok',
    });
  });

  it('falls back to invalid_token on an unknown 400 error shape', async () => {
    const { event } = buildEvent({
      fields: { email: 'a@b.test', token: 'tok', password: 'p', confirmPassword: 'p' },
      response: new Response(JSON.stringify({ error: 'something_else' }), { status: 400 }),
    });

    const result = (await run(event)) as { status: number; data: { error: string } };
    expect(result.status).toBe(400);
    expect(result.data.error).toBe('invalid_token');
  });

  it('falls back to invalid_token when the 400 body is not valid JSON', async () => {
    const { event } = buildEvent({
      fields: { email: 'a@b.test', token: 'tok', password: 'p', confirmPassword: 'p' },
      response: new Response('boom', { status: 400 }),
    });

    const result = (await run(event)) as { status: number; data: { error: string } };
    expect(result.status).toBe(400);
    expect(result.data.error).toBe('invalid_token');
  });

  it('returns fail(404, reset_unavailable) echoing email and token when the reset flow is disabled', async () => {
    const { event } = buildEvent({
      fields: { email: 'a@b.test', token: 'tok-9', password: 'p', confirmPassword: 'p' },
      response: new Response(null, { status: 404 }),
    });

    const result = (await run(event)) as {
      status: number;
      data: { error: string; email: string; token: string };
    };
    expect(result.status).toBe(404);
    expect(result.data).toEqual({
      error: 'reset_unavailable',
      email: 'a@b.test',
      token: 'tok-9',
    });
  });
});
