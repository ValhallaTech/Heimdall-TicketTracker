/**
 * Unit spec for the tickets list loader (`/tickets` +page.server.ts).
 *
 * Mocks `$env/dynamic/private` and a fake load event (locals/url/fetch). Covers
 * the unauth redirect, the query-string construction (defaults + overrides +
 * empty-search omission), the bearer token, and the parsed page return.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';

import { load } from './+page.server';
import { TicketStatus, TicketPriority } from '$lib/api/tickets';

vi.mock('$env/dynamic/private', () => ({ env: {} as Record<string, string | undefined> }));

interface RedirectError {
  status: number;
  location: string;
}
function isRedirect(v: unknown): v is RedirectError {
  return typeof v === 'object' && v !== null && 'location' in v;
}

function wireTicket() {
  return {
    Id: 1,
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
  };
}

function buildEvent(opts: { accessToken: string | null; search?: string; response?: Response }) {
  const fetchMock = vi.fn(
    async () => opts.response ?? new Response(JSON.stringify({}), { status: 200 }),
  );
  const event = {
    locals: { accessToken: opts.accessToken },
    url: new URL(`http://localhost/tickets${opts.search ?? ''}`),
    fetch: fetchMock,
  } as never;
  return { event, fetchMock };
}

async function run(event: never): Promise<unknown> {
  return (load as (e: never) => Promise<unknown>)(event);
}

afterEach(() => vi.clearAllMocks());

describe('/tickets +page.server.ts load', () => {
  it('redirects to /login?returnUrl=… when locals.accessToken is null', async () => {
    const { event } = buildEvent({ accessToken: null });

    try {
      await run(event);
      throw new Error('expected redirect');
    } catch (e) {
      expect(isRedirect(e)).toBe(true);
      if (isRedirect(e)) {
        expect(e.status).toBe(303);
        expect(e.location).toBe('/login?returnUrl=%2Ftickets');
      }
    }
  });

  it('GETs /api/v1/tickets with the bearer token and applies defaults', async () => {
    const { event, fetchMock } = buildEvent({
      accessToken: 'tok',
      response: new Response(JSON.stringify({ Items: [wireTicket()], TotalCount: 1 }), {
        status: 200,
      }),
    });

    await run(event);

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toContain('http://127.0.0.1:5000/api/v1/tickets');
    expect(url).toContain('page=1');
    expect(url).toContain('pageSize=25');
    expect(url).toContain('sortField=DateCreated');
    expect(url).toContain('sortDirection=Descending');
    expect(url).not.toContain('searchText');
    expect(new Headers(init.headers).get('authorization')).toBe('Bearer ' + 'tok');
  });

  it('reads page/pageSize/searchText/sortField/sortDirection from the URL', async () => {
    const { event, fetchMock } = buildEvent({
      accessToken: 'tok',
      search: '?page=3&pageSize=10&searchText=down&sortField=Title&sortDirection=Ascending',
    });

    await run(event);

    const url = (fetchMock.mock.calls[0] as [string])[0];
    expect(url).toContain('page=3');
    expect(url).toContain('pageSize=10');
    expect(url).toContain('searchText=down');
    expect(url).toContain('sortField=Title');
    expect(url).toContain('sortDirection=Ascending');
  });

  it('returns the parsed PagedResult<Ticket> and echoed query as page data', async () => {
    const { event } = buildEvent({
      accessToken: 'tok',
      search: '?searchText=hello',
      response: new Response(
        JSON.stringify({
          Items: [wireTicket()],
          TotalCount: 1,
          Page: 1,
          PageSize: 25,
          TotalPages: 1,
        }),
        { status: 200 },
      ),
    });

    const data = (await run(event)) as {
      tickets: { Items: unknown[]; TotalCount: number };
      query: { searchText: string };
    };
    expect(data.tickets.Items).toHaveLength(1);
    expect(data.tickets.TotalCount).toBe(1);
    expect(data.query.searchText).toBe('hello');
  });
});
