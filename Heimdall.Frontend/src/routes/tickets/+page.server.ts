/**
 * Tickets list loader — ports the data fetch from `Tickets.razor` (Phase 6.4).
 *
 * Authenticated: uses the bearer token from `event.locals.accessToken` (set by
 * `hooks.server.ts`). When unauthenticated, redirects to `/login` with the
 * current path as `returnUrl`. Reads paging / search / sort from the query
 * string (so the controls in `+page.svelte` are plain links / a GET form and
 * keep working without client JS), calls `GET /api/v1/tickets`, and returns the
 * `PagedResult<Ticket>` plus the echoed query params as page data.
 */
import { env } from '$env/dynamic/private';
import { redirect } from '@sveltejs/kit';
import { asTicketPage, fetchJson, type PagedResult, type Ticket } from '$lib/api/tickets';
import type { PageServerLoad } from './$types';

const DEFAULT_INTERNAL_API_ORIGIN = 'http://127.0.0.1:5000';

function internalApiOrigin(): string {
  return env.INTERNAL_API_ORIGIN ?? DEFAULT_INTERNAL_API_ORIGIN;
}

/** Query parameters echoed back to the page so the controls stay in sync. */
export interface TicketsQuery {
  page: number;
  pageSize: number;
  searchText: string;
  sortField: string;
  sortDirection: string;
}

const DEFAULT_QUERY: TicketsQuery = {
  page: 1,
  pageSize: 25,
  searchText: '',
  sortField: 'DateCreated',
  sortDirection: 'Descending',
};

/** Parse a positive integer query param, falling back to a default. */
function parsePositiveInt(raw: string | null, fallback: number): number {
  if (raw === null) {
    return fallback;
  }
  const parsed = Number.parseInt(raw, 10);
  return Number.isNaN(parsed) || parsed < 1 ? fallback : parsed;
}

export const load: PageServerLoad = async (event) => {
  const accessToken = event.locals.accessToken;
  if (accessToken === null) {
    redirect(303, '/login?returnUrl=' + encodeURIComponent(event.url.pathname));
  }

  const { searchParams } = event.url;
  const query: TicketsQuery = {
    page: parsePositiveInt(searchParams.get('page'), DEFAULT_QUERY.page),
    pageSize: parsePositiveInt(searchParams.get('pageSize'), DEFAULT_QUERY.pageSize),
    searchText: searchParams.get('searchText') ?? DEFAULT_QUERY.searchText,
    sortField: searchParams.get('sortField') ?? DEFAULT_QUERY.sortField,
    sortDirection: searchParams.get('sortDirection') ?? DEFAULT_QUERY.sortDirection,
  };

  const apiUrl = new URL(`${internalApiOrigin()}/api/v1/tickets`);
  apiUrl.searchParams.set('page', String(query.page));
  apiUrl.searchParams.set('pageSize', String(query.pageSize));
  if (query.searchText.length > 0) {
    apiUrl.searchParams.set('searchText', query.searchText);
  }
  apiUrl.searchParams.set('sortField', query.sortField);
  apiUrl.searchParams.set('sortDirection', query.sortDirection);

  const result = await fetchJson(event.fetch, apiUrl.toString(), accessToken);
  const tickets: PagedResult<Ticket> = asTicketPage(result.data);

  return { tickets, query };
};
