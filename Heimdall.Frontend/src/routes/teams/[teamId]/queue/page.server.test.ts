/**
 * Unit spec for the team queue loader (`/teams/[teamId]/queue` +page.server.ts).
 *
 * Mocks `$env/dynamic/private` and a fake load event (locals/params/url/fetch).
 * Covers the unauth redirect, the bearer + `pageSize=100` request, the
 * client-side `TeamId` filter, and the empty-array fallback on an unparseable
 * body.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';

import { load } from './+page.server';
import { TicketPriority, TicketStatus, type Ticket } from '$lib/api/tickets';

vi.mock('$env/dynamic/private', () => ({ env: {} as Record<string, string | undefined> }));

interface RedirectError {
  status: number;
  location: string;
}
function isRedirect(v: unknown): v is RedirectError {
  return typeof v === 'object' && v !== null && 'location' in v;
}

function wireTicket(overrides: Partial<Ticket> = {}): Ticket {
  return {
    Id: 1,
    Title: 'T',
    Description: 'd',
    Status: TicketStatus.Open,
    Priority: TicketPriority.Low,
    ProjectId: 'p',
    TeamId: 'team-a',
    ReporterId: 'r',
    AssigneeId: null,
    DateCreated: '2025-01-05T10:00:00Z',
    DateUpdated: '2025-01-05T10:00:00Z',
    ...overrides,
  };
}

function buildEvent(opts: { accessToken: string | null; teamId?: string; response?: Response }) {
  const fetchMock = vi.fn(
    async () => opts.response ?? new Response(JSON.stringify({ Items: [] }), { status: 200 }),
  );
  const teamId = opts.teamId ?? 'team-a';
  const event = {
    locals: { accessToken: opts.accessToken },
    params: { teamId },
    url: new URL(`http://localhost/teams/${teamId}/queue`),
    fetch: fetchMock,
  } as never;
  return { event, fetchMock };
}

async function run(event: never): Promise<unknown> {
  return (load as (e: never) => Promise<unknown>)(event);
}

afterEach(() => vi.clearAllMocks());

describe('/teams/[teamId]/queue +page.server.ts load', () => {
  it('redirects to /login?returnUrl=… when locals.accessToken is null', async () => {
    const { event } = buildEvent({ accessToken: null, teamId: 'team-a' });

    try {
      await run(event);
      throw new Error('expected redirect');
    } catch (e) {
      expect(isRedirect(e)).toBe(true);
      if (isRedirect(e)) {
        expect(e.status).toBe(303);
        expect(e.location).toBe('/login?returnUrl=%2Fteams%2Fteam-a%2Fqueue');
      }
    }
  });

  it('GETs /api/v1/tickets with the bearer token and pageSize=100', async () => {
    const { event, fetchMock } = buildEvent({ accessToken: 'tok' });

    await run(event);

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toContain('http://127.0.0.1:5000/api/v1/tickets');
    expect(url).toContain('pageSize=100');
    expect(new Headers(init.headers).get('authorization')).toBe('Bearer ' + 'tok');
  });

  it('filters the page to tickets whose TeamId matches the route param', async () => {
    const { event } = buildEvent({
      accessToken: 'tok',
      teamId: 'team-a',
      response: new Response(
        JSON.stringify({
          Items: [
            wireTicket({ Id: 1, TeamId: 'team-a' }),
            wireTicket({ Id: 2, TeamId: 'team-b' }),
            wireTicket({ Id: 3, TeamId: 'team-a' }),
          ],
          TotalCount: 3,
        }),
        { status: 200 },
      ),
    });

    const data = (await run(event)) as { tickets: Ticket[]; teamId: string };
    expect(data.teamId).toBe('team-a');
    expect(data.tickets.map((t) => t.Id)).toEqual([1, 3]);
  });

  it('returns an empty list when the response body is unparseable', async () => {
    const { event } = buildEvent({
      accessToken: 'tok',
      teamId: 'team-a',
      response: new Response('not json', { status: 200 }),
    });

    const data = (await run(event)) as { tickets: Ticket[]; teamId: string };
    expect(data.tickets).toEqual([]);
    expect(data.teamId).toBe('team-a');
  });
});
