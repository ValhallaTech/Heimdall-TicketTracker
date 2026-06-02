/**
 * Unit spec for the ticket edit loader + update action
 * (`/tickets/[id]` +page.server.ts).
 *
 * Loader: unauth redirect, 404/403 → error(), unparseable → error(404), success
 * → { ticket }. Action: unauth redirect, PUT with Id forced + empty-assignee →
 * null, 204 redirect, 404 error, 422 fail, generic fail.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { RequestEvent } from '@sveltejs/kit';

import { load, actions } from './+page.server';
import { TicketStatus, TicketPriority } from '$lib/api/tickets';

vi.mock('$env/dynamic/private', () => ({ env: {} as Record<string, string | undefined> }));

interface HttpError {
  status: number;
  body?: unknown;
}
interface RedirectError {
  status: number;
  location: string;
}
function isRedirect(v: unknown): v is RedirectError {
  return typeof v === 'object' && v !== null && 'location' in v;
}
function isError(v: unknown): v is HttpError {
  return typeof v === 'object' && v !== null && 'status' in v && !('location' in v);
}

function wireTicket(over: Record<string, unknown> = {}) {
  return {
    Id: 5,
    Title: 'T',
    Description: 'd',
    Status: TicketStatus.Open,
    Priority: TicketPriority.Low,
    ProjectId: 'p',
    TeamId: 't',
    ReporterId: 'r',
    AssigneeId: null,
    DateCreated: '2025-01-05T10:00:00Z',
    DateUpdated: '2025-01-05T10:00:00Z',
    ...over,
  };
}

function loadEvent(opts: { accessToken: string | null; id?: string; response?: Response }) {
  const fetchMock = vi.fn(
    async () => opts.response ?? new Response(JSON.stringify(wireTicket()), { status: 200 }),
  );
  const event = {
    locals: { accessToken: opts.accessToken },
    url: new URL('http://localhost/tickets/5'),
    params: { id: opts.id ?? '5' },
    fetch: fetchMock,
  } as unknown as RequestEvent;
  return { event, fetchMock };
}

function actionEvent(opts: {
  accessToken: string | null;
  id?: string;
  fields?: Record<string, string>;
  response?: Response;
}) {
  const form = new FormData();
  for (const [k, v] of Object.entries(opts.fields ?? {})) {
    form.set(k, v);
  }
  const fetchMock = vi.fn(async () => opts.response ?? new Response(null, { status: 204 }));
  const event = {
    locals: { accessToken: opts.accessToken },
    url: new URL('http://localhost/tickets/5'),
    params: { id: opts.id ?? '5' },
    request: { formData: async () => form },
    fetch: fetchMock,
  } as unknown as RequestEvent;
  return { event, fetchMock };
}

async function runLoad(event: RequestEvent): Promise<unknown> {
  return (load as (e: RequestEvent) => Promise<unknown>)(event);
}
async function runAction(event: RequestEvent): Promise<unknown> {
  return (actions.default as (e: RequestEvent) => Promise<unknown>)(event);
}

afterEach(() => vi.clearAllMocks());

describe('/tickets/[id] +page.server.ts load', () => {
  it('redirects to /login when accessToken is null', async () => {
    const { event } = loadEvent({ accessToken: null });
    try {
      await runLoad(event);
      throw new Error('expected redirect');
    } catch (e) {
      expect(isRedirect(e) && e.location).toBe('/login?returnUrl=%2Ftickets%2F5');
    }
  });

  it('GETs /api/v1/tickets/{id} with the bearer token and returns { ticket }', async () => {
    const { event, fetchMock } = loadEvent({
      accessToken: 'tok',
      response: new Response(JSON.stringify(wireTicket({ Id: 5, Title: 'Loaded' })), {
        status: 200,
      }),
    });

    const data = (await runLoad(event)) as { ticket: { Id: number; Title: string } };

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('http://127.0.0.1:5000/api/v1/tickets/5');
    expect(new Headers(init.headers).get('authorization')).toBe('Bearer ' + 'tok');
    expect(data.ticket.Title).toBe('Loaded');
  });

  it('throws error(404) when the API responds 404', async () => {
    const { event } = loadEvent({
      accessToken: 'tok',
      response: new Response('{}', { status: 404 }),
    });
    try {
      await runLoad(event);
      throw new Error('expected error');
    } catch (e) {
      expect(isError(e) && e.status).toBe(404);
    }
  });

  it('throws error(403) when the API responds 403', async () => {
    const { event } = loadEvent({
      accessToken: 'tok',
      response: new Response('{}', { status: 403 }),
    });
    try {
      await runLoad(event);
      throw new Error('expected error');
    } catch (e) {
      expect(isError(e) && e.status).toBe(403);
    }
  });

  it('throws error(404) when the body cannot be parsed as a ticket', async () => {
    const { event } = loadEvent({
      accessToken: 'tok',
      response: new Response(JSON.stringify({ not: 'a ticket' }), { status: 200 }),
    });
    try {
      await runLoad(event);
      throw new Error('expected error');
    } catch (e) {
      expect(isError(e) && e.status).toBe(404);
    }
  });
});

describe('/tickets/[id] +page.server.ts default action', () => {
  it('redirects to /login when accessToken is null', async () => {
    const { event } = actionEvent({ accessToken: null });
    try {
      await runAction(event);
      throw new Error('expected redirect');
    } catch (e) {
      expect(isRedirect(e) && e.location).toBe('/login?returnUrl=%2Ftickets%2F5');
    }
  });

  it('PUTs PascalCase JSON with Id forced to the route id and empty assignee → null', async () => {
    const { event, fetchMock } = actionEvent({
      accessToken: 'tok',
      id: '5',
      fields: {
        title: 'New',
        description: 'd',
        teamId: 't',
        projectId: 'p',
        reporterId: 'r',
        assigneeId: '',
        status: '2',
        priority: '3',
      },
      response: new Response(null, { status: 204 }),
    });

    await expect(runAction(event)).rejects.toBeDefined(); // 204 redirect

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('http://127.0.0.1:5000/api/v1/tickets/5');
    expect(init.method).toBe('PUT');
    const body = JSON.parse(init.body as string);
    expect(body.Id).toBe(5);
    expect(body.AssigneeId).toBeNull();
    expect(body.Status).toBe(2);
  });

  it('redirects (303) to /tickets/{id} on 204', async () => {
    const { event } = actionEvent({
      accessToken: 'tok',
      id: '5',
      fields: { title: 'x', description: 'd', teamId: 't', projectId: 'p', reporterId: 'r' },
      response: new Response(null, { status: 204 }),
    });
    try {
      await runAction(event);
      throw new Error('expected redirect');
    } catch (e) {
      expect(isRedirect(e) && e.location).toBe('/tickets/5');
    }
  });

  it('throws error(404) when the API responds 404', async () => {
    const { event } = actionEvent({
      accessToken: 'tok',
      fields: { title: 'x', description: 'd', teamId: 't', projectId: 'p', reporterId: 'r' },
      response: new Response('{}', { status: 404 }),
    });
    try {
      await runAction(event);
      throw new Error('expected error');
    } catch (e) {
      expect(isError(e) && e.status).toBe(404);
    }
  });

  it('returns fail(422, { errors, values }) on a validation problem+json', async () => {
    const { event } = actionEvent({
      accessToken: 'tok',
      fields: { title: '', description: 'd', teamId: 't', projectId: 'p', reporterId: 'r' },
      response: new Response(JSON.stringify({ errors: { Title: ['Required'] } }), { status: 422 }),
    });
    const result = (await runAction(event)) as {
      status: number;
      data: { errors: Record<string, string[]> };
    };
    expect(result.status).toBe(422);
    expect(result.data.errors.Title).toEqual(['Required']);
  });

  it('returns a fail with a generic error on other non-success statuses', async () => {
    const { event } = actionEvent({
      accessToken: 'tok',
      fields: { title: 'x', description: 'd', teamId: 't', projectId: 'p', reporterId: 'r' },
      response: new Response('boom', { status: 500 }),
    });
    const result = (await runAction(event)) as {
      status: number;
      data: { errors: Record<string, string[]> };
    };
    expect(result.status).toBe(500);
    expect(result.data.errors['']).toBeDefined();
  });
});
