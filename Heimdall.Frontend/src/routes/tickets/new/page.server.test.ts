/**
 * Unit spec for the new-ticket action (`/tickets/new` +page.server.ts).
 *
 * Covers the unauth redirect, the PascalCase POST with the bearer token and
 * ReporterId from locals.user.id, the 201 → /tickets/{id} redirect (and the
 * unparseable-body fallback to /tickets), the 422 fail, and the generic fail.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { RequestEvent } from '@sveltejs/kit';

import { actions } from './+page.server';

vi.mock('$env/dynamic/private', () => ({ env: {} as Record<string, string | undefined> }));

interface RedirectError {
  status: number;
  location: string;
}
function isRedirect(v: unknown): v is RedirectError {
  return typeof v === 'object' && v !== null && 'location' in v;
}

function buildEvent(opts: {
  accessToken: string | null;
  userId?: string;
  fields?: Record<string, string>;
  response?: Response;
}): { event: RequestEvent; fetchMock: ReturnType<typeof vi.fn> } {
  const form = new FormData();
  for (const [k, v] of Object.entries(opts.fields ?? {})) {
    form.set(k, v);
  }
  const fetchMock = vi.fn(async () => opts.response ?? new Response(null, { status: 201 }));
  const event = {
    locals: { accessToken: opts.accessToken, user: opts.userId ? { id: opts.userId } : undefined },
    url: new URL('http://localhost/tickets/new'),
    request: { formData: async () => form },
    fetch: fetchMock,
  } as unknown as RequestEvent;
  return { event, fetchMock };
}

async function run(event: RequestEvent): Promise<unknown> {
  return (actions.default as (e: RequestEvent) => Promise<unknown>)(event);
}

afterEach(() => vi.clearAllMocks());

describe('/tickets/new +page.server.ts default action', () => {
  it('redirects to /login?returnUrl=… when locals.accessToken is null', async () => {
    const { event } = buildEvent({ accessToken: null });

    try {
      await run(event);
      throw new Error('expected redirect');
    } catch (e) {
      expect(isRedirect(e) && e.location).toBe('/login?returnUrl=%2Ftickets%2Fnew');
    }
  });

  it('POSTs PascalCase JSON with the bearer token and ReporterId from locals.user.id', async () => {
    const { event, fetchMock } = buildEvent({
      accessToken: 'tok',
      userId: 'user-9',
      fields: {
        title: 'New',
        description: 'desc',
        teamId: 't',
        projectId: 'p',
        status: '1',
        priority: '2',
      },
      response: new Response(
        JSON.stringify({
          ...{},
          Id: 42,
          Title: 'New',
          Description: 'desc',
          Status: 1,
          Priority: 2,
          ProjectId: 'p',
          TeamId: 't',
          ReporterId: 'user-9',
          AssigneeId: null,
          DateCreated: 'x',
          DateUpdated: 'y',
        }),
        { status: 201 },
      ),
    });

    await expect(run(event)).rejects.toBeDefined(); // redirect on 201

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('http://127.0.0.1:5000/api/v1/tickets');
    expect(init.method).toBe('POST');
    expect(new Headers(init.headers).get('authorization')).toBe('Bearer ' + 'tok');
    const body = JSON.parse(init.body as string);
    expect(body.Title).toBe('New');
    expect(body.ReporterId).toBe('user-9');
    expect(body.Status).toBe(1);
  });

  it('redirects (303) to /tickets/{created.Id} on 201', async () => {
    const { event } = buildEvent({
      accessToken: 'tok',
      userId: 'u',
      fields: { title: 'New', description: 'd', teamId: 't', projectId: 'p' },
      response: new Response(
        JSON.stringify({
          Id: 42,
          Title: 'New',
          Description: 'd',
          Status: 0,
          Priority: 1,
          ProjectId: 'p',
          TeamId: 't',
          ReporterId: 'u',
          AssigneeId: null,
          DateCreated: 'x',
          DateUpdated: 'y',
        }),
        { status: 201 },
      ),
    });

    try {
      await run(event);
      throw new Error('expected redirect');
    } catch (e) {
      expect(isRedirect(e) && e.status).toBe(303);
      expect(isRedirect(e) && e.location).toBe('/tickets/42');
    }
  });

  it('redirects to /tickets when the 201 body is unparseable', async () => {
    const { event } = buildEvent({
      accessToken: 'tok',
      userId: 'u',
      fields: { title: 'New', description: 'd', teamId: 't', projectId: 'p' },
      response: new Response('not json', { status: 201 }),
    });

    try {
      await run(event);
      throw new Error('expected redirect');
    } catch (e) {
      expect(isRedirect(e) && e.location).toBe('/tickets');
    }
  });

  it('returns fail(422, { errors, values }) on a validation problem+json', async () => {
    const { event } = buildEvent({
      accessToken: 'tok',
      userId: 'u',
      fields: { title: '', description: 'd', teamId: 't', projectId: 'p' },
      response: new Response(JSON.stringify({ errors: { Title: ['Required'] } }), { status: 422 }),
    });

    const result = (await run(event)) as {
      status: number;
      data: { errors: Record<string, string[]> };
    };
    expect(result.status).toBe(422);
    expect(result.data.errors.Title).toEqual(['Required']);
  });

  it('returns a fail with a generic error on other non-success statuses', async () => {
    const { event } = buildEvent({
      accessToken: 'tok',
      userId: 'u',
      fields: { title: 'x', description: 'd', teamId: 't', projectId: 'p' },
      response: new Response('boom', { status: 500 }),
    });

    const result = (await run(event)) as {
      status: number;
      data: { errors: Record<string, string[]> };
    };
    expect(result.status).toBe(500);
    expect(result.data.errors['']).toBeDefined();
  });
});
