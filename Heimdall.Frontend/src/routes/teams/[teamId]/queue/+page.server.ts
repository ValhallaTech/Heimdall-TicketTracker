/**
 * Team queue loader — ports `Teams/Queue.razor` (Phase 6.4).
 *
 * Authenticated: requires `event.locals.accessToken`; redirects to `/login`
 * otherwise. Fetches a large page of tickets (`GET /api/v1/tickets?pageSize=100`,
 * which the API already FGA-filters to tickets the caller may view) and filters
 * client-side to the requested team. Returns the team's tickets plus the teamId.
 *
 * NOTE: team scoping is a client-side filter for now; a server-side
 * `teamId` query-param filter on `GET /api/v1/tickets` is a follow-up so the
 * page no longer over-fetches and is not bounded by the 100-item page size.
 */
import { env } from '$env/dynamic/private';
import { redirect } from '@sveltejs/kit';
import { asTicketPage, fetchJson, type Ticket } from '$lib/api/tickets';
import type { PageServerLoad } from './$types';

const DEFAULT_INTERNAL_API_ORIGIN = 'http://127.0.0.1:5000';
const QUEUE_PAGE_SIZE = 100;

function internalApiOrigin(): string {
  return env.INTERNAL_API_ORIGIN ?? DEFAULT_INTERNAL_API_ORIGIN;
}

export const load: PageServerLoad = async (event) => {
  const accessToken = event.locals.accessToken;
  if (accessToken === null) {
    redirect(303, '/login?returnUrl=' + encodeURIComponent(event.url.pathname));
  }

  const apiUrl = new URL(`${internalApiOrigin()}/api/v1/tickets`);
  apiUrl.searchParams.set('pageSize', String(QUEUE_PAGE_SIZE));

  const result = await fetchJson(event.fetch, apiUrl.toString(), accessToken);
  const page = asTicketPage(result.data);

  const teamId = event.params.teamId;
  const tickets: Ticket[] = page.Items.filter((ticket) => ticket.TeamId === teamId);

  return { tickets, teamId };
};
