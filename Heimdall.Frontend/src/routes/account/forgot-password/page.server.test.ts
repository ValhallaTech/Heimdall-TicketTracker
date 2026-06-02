/**
 * Unit spec for the forgot-password form action
 * (`/account/forgot-password` +page.server.ts).
 *
 * Mocks `$env/dynamic/private` (origin) and a fake `RequestEvent`. Asserts the
 * `{ email }` JSON payload forwarded to `POST /api/v1/auth/forgot-password`, the
 * anti-enumeration property (ANY 200 body shape redirects to the same generic
 * confirmation page regardless of whether the email was known), the 404 →
 * `reset_unavailable` branch, and the generic `reset_failed` branch for any
 * other non-OK status. `redirect` throws, so success is asserted via a catch.
 * Mirrors `login/page.server.test.ts`.
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

describe('/account/forgot-password +page.server.ts default action', () => {
  it('POSTs JSON { email } to {origin}/api/v1/auth/forgot-password', async () => {
    const { event, fetchMock } = buildEvent({
      fields: { email: 'odin@asgard.test' },
      response: new Response(JSON.stringify({ status: 'ok' }), { status: 200 }),
    });

    await expect(run(event)).rejects.toBeDefined(); // redirect throws on success

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('http://127.0.0.1:5000/api/v1/auth/forgot-password');
    expect(init.method).toBe('POST');
    expect(init.headers).toEqual({ 'content-type': 'application/json' });
    expect(JSON.parse(init.body as string)).toEqual({ email: 'odin@asgard.test' });
  });

  it('resolves the API origin from env.INTERNAL_API_ORIGIN', async () => {
    env.INTERNAL_API_ORIGIN = 'http://api.internal:6000';
    const { event, fetchMock } = buildEvent({
      fields: { email: 'a@b.test' },
      response: new Response(JSON.stringify({ status: 'ok' }), { status: 200 }),
    });

    await expect(run(event)).rejects.toBeDefined();

    expect((fetchMock.mock.calls[0] as [string])[0]).toBe(
      'http://api.internal:6000/api/v1/auth/forgot-password',
    );
  });

  it('redirects (303) to the confirmation page for a known account', async () => {
    const { event } = buildEvent({
      fields: { email: 'known@asgard.test' },
      response: new Response(JSON.stringify({ status: 'ok' }), { status: 200 }),
    });

    try {
      await run(event);
      throw new Error('expected redirect');
    } catch (e) {
      expect(isRedirect(e)).toBe(true);
      if (isRedirect(e)) {
        expect(e.status).toBe(303);
        expect(e.location).toBe('/account/forgot-password/confirmation');
      }
    }
  });

  it('redirects (303) to the SAME confirmation page for an unknown account (anti-enumeration)', async () => {
    // A different/empty 200 body shape must produce the identical redirect, so a
    // caller cannot distinguish a known from an unknown email.
    const { event } = buildEvent({
      fields: { email: 'unknown@nowhere.test' },
      response: new Response(null, { status: 200 }),
    });

    try {
      await run(event);
      throw new Error('expected redirect');
    } catch (e) {
      expect(isRedirect(e)).toBe(true);
      if (isRedirect(e)) {
        expect(e.status).toBe(303);
        expect(e.location).toBe('/account/forgot-password/confirmation');
      }
    }
  });

  it('returns fail(404, reset_unavailable) when the email flow is disabled', async () => {
    const { event } = buildEvent({
      fields: { email: 'a@b.test' },
      response: new Response(null, { status: 404 }),
    });

    const result = (await run(event)) as { status: number; data: { error: string; email: string } };
    expect(result.status).toBe(404);
    expect(result.data).toEqual({ error: 'reset_unavailable', email: 'a@b.test' });
  });

  it('returns fail(400, reset_failed) on any other non-OK status', async () => {
    const { event } = buildEvent({
      fields: { email: 'a@b.test' },
      response: new Response('boom', { status: 500 }),
    });

    const result = (await run(event)) as { status: number; data: { error: string; email: string } };
    expect(result.status).toBe(400);
    expect(result.data).toEqual({ error: 'reset_failed', email: 'a@b.test' });
  });
});
