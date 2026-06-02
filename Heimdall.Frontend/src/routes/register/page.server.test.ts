/**
 * Unit spec for the register form action (`/register` +page.server.ts).
 *
 * Mocks `$env/dynamic/private` (origin) and a fake `RequestEvent` (request/fetch)
 * and asserts the JSON payload forwarded to `POST /api/v1/auth/register`, the
 * 200 → redirect(303, '/register/confirmation'), the 400 error-code → fail()
 * mappings (with email repopulated and optional identity `codes`), and the 404 →
 * `registration_unavailable` branch. `redirect` throws, so success is asserted
 * via rejects; the `fail` branches return a plain payload. Mirrors
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

describe('/register +page.server.ts default action', () => {
  it('POSTs JSON { email, password, confirmPassword } to {origin}/api/v1/auth/register', async () => {
    const { event, fetchMock } = buildEvent({
      fields: { email: 'odin@asgard.test', password: 'secret', confirmPassword: 'secret' },
      response: new Response(null, { status: 200 }),
    });

    await expect(run(event)).rejects.toBeDefined(); // redirect throws on success

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('http://127.0.0.1:5000/api/v1/auth/register');
    expect(init.method).toBe('POST');
    expect(init.headers).toEqual({ 'content-type': 'application/json' });
    expect(JSON.parse(init.body as string)).toEqual({
      email: 'odin@asgard.test',
      password: 'secret',
      confirmPassword: 'secret',
    });
  });

  it('resolves the API origin from env.INTERNAL_API_ORIGIN', async () => {
    env.INTERNAL_API_ORIGIN = 'http://api.internal:6000';
    const { event, fetchMock } = buildEvent({
      fields: { email: 'a@b.test', password: 'p', confirmPassword: 'p' },
      response: new Response(null, { status: 200 }),
    });

    await expect(run(event)).rejects.toBeDefined();

    expect((fetchMock.mock.calls[0] as [string])[0]).toBe(
      'http://api.internal:6000/api/v1/auth/register',
    );
  });

  it('redirects (303) to /register/confirmation on success', async () => {
    const { event } = buildEvent({
      fields: { email: 'a@b.test', password: 'p', confirmPassword: 'p' },
      response: new Response(JSON.stringify({ status: 'confirmation_pending' }), { status: 200 }),
    });

    try {
      await run(event);
      throw new Error('expected redirect');
    } catch (e) {
      expect(isRedirect(e)).toBe(true);
      if (isRedirect(e)) {
        expect(e.status).toBe(303);
        expect(e.location).toBe('/register/confirmation');
      }
    }
  });

  it('returns fail(400, invalid_email) and repopulates email on 400 invalid_email', async () => {
    const { event } = buildEvent({
      fields: { email: 'not-an-email', password: 'p', confirmPassword: 'p' },
      response: new Response(JSON.stringify({ error: 'invalid_email' }), { status: 400 }),
    });

    const result = (await run(event)) as { status: number; data: { error: string; email: string } };
    expect(result.status).toBe(400);
    expect(result.data).toEqual({ error: 'invalid_email', email: 'not-an-email' });
  });

  it('returns fail(400, password_mismatch) and repopulates email on 400 password_mismatch', async () => {
    const { event } = buildEvent({
      fields: { email: 'a@b.test', password: 'p', confirmPassword: 'q' },
      response: new Response(JSON.stringify({ error: 'password_mismatch' }), { status: 400 }),
    });

    const result = (await run(event)) as { status: number; data: { error: string; email: string } };
    expect(result.status).toBe(400);
    expect(result.data).toEqual({ error: 'password_mismatch', email: 'a@b.test' });
  });

  it('returns fail(400, registration_failed) with the API identity codes surfaced', async () => {
    const { event } = buildEvent({
      fields: { email: 'a@b.test', password: 'p', confirmPassword: 'p' },
      response: new Response(
        JSON.stringify({
          error: 'registration_failed',
          codes: ['PasswordTooShort', 'PasswordRequiresDigit'],
        }),
        { status: 400 },
      ),
    });

    const result = (await run(event)) as {
      status: number;
      data: { error: string; email: string; codes?: string[] };
    };
    expect(result.status).toBe(400);
    expect(result.data).toEqual({
      error: 'registration_failed',
      email: 'a@b.test',
      codes: ['PasswordTooShort', 'PasswordRequiresDigit'],
    });
  });

  it('falls back to registration_failed (codes undefined) on an unknown 400 error shape', async () => {
    const { event } = buildEvent({
      fields: { email: 'a@b.test', password: 'p', confirmPassword: 'p' },
      response: new Response(JSON.stringify({ error: 'something_unexpected' }), { status: 400 }),
    });

    const result = (await run(event)) as {
      status: number;
      data: { error: string; email: string; codes?: string[] };
    };
    expect(result.status).toBe(400);
    expect(result.data.error).toBe('registration_failed');
    expect(result.data.email).toBe('a@b.test');
    expect(result.data.codes).toBeUndefined();
  });

  it('ignores a non-string-array codes field on a registration_failed response', async () => {
    const { event } = buildEvent({
      fields: { email: 'a@b.test', password: 'p', confirmPassword: 'p' },
      response: new Response(JSON.stringify({ error: 'registration_failed', codes: [1, 2, 3] }), {
        status: 400,
      }),
    });

    const result = (await run(event)) as {
      status: number;
      data: { error: string; codes?: string[] };
    };
    expect(result.data.error).toBe('registration_failed');
    expect(result.data.codes).toBeUndefined();
  });

  it('falls back to registration_failed when the 400 body is not valid JSON', async () => {
    const { event } = buildEvent({
      fields: { email: 'a@b.test', password: 'p', confirmPassword: 'p' },
      response: new Response('boom', { status: 400 }),
    });

    const result = (await run(event)) as { status: number; data: { error: string } };
    expect(result.status).toBe(400);
    expect(result.data.error).toBe('registration_failed');
  });

  it('returns fail(404, registration_unavailable) when the API has registration disabled', async () => {
    const { event } = buildEvent({
      fields: { email: 'a@b.test', password: 'p', confirmPassword: 'p' },
      response: new Response(null, { status: 404 }),
    });

    const result = (await run(event)) as { status: number; data: { error: string; email: string } };
    expect(result.status).toBe(404);
    expect(result.data).toEqual({ error: 'registration_unavailable', email: 'a@b.test' });
  });
});
