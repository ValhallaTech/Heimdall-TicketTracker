/**
 * Ticket edit loader + update action — ports `TicketEdit.razor` (Phase 6.4).
 *
 * Authenticated: requires `event.locals.accessToken`; redirects to `/login`
 * otherwise. The loader fetches the ticket (`GET /api/v1/tickets/{id}`) and maps
 * the API status codes to SvelteKit errors (404 → `error(404)`, 403 →
 * `error(403)`). The default action PUTs the edited PascalCase JSON back
 * (`PUT /api/v1/tickets/{id}`, with `Id` forced to the route id) and maps 204 →
 * redirect, 422 → field errors, 404 → `error(404)`.
 *
 * Deviation: Team / Project / Reporter / Assignee are edited as raw id fields
 * (no lookup API yet) — see the new-ticket loader NOTE.
 */
import { env } from '$env/dynamic/private';
import { error, fail, redirect } from '@sveltejs/kit';
import { asTicket, fetchJson, parseValidationErrors } from '$lib/api/tickets';
import type { Actions, PageServerLoad, RequestEvent } from './$types';

const DEFAULT_INTERNAL_API_ORIGIN = 'http://127.0.0.1:5000';

function internalApiOrigin(): string {
  return env.INTERNAL_API_ORIGIN ?? DEFAULT_INTERNAL_API_ORIGIN;
}

function ticketUrl(id: string): string {
  return `${internalApiOrigin()}/api/v1/tickets/${encodeURIComponent(id)}`;
}

/** Read a string form field, trimming to '' when absent. */
function readString(form: FormData, key: string): string {
  const value = form.get(key);
  return typeof value === 'string' ? value : '';
}

/** Read an integer (enum) form field, falling back to a default. */
function readInt(form: FormData, key: string, fallback: number): number {
  const value = form.get(key);
  if (typeof value !== 'string') {
    return fallback;
  }
  const parsed = Number.parseInt(value, 10);
  return Number.isNaN(parsed) ? fallback : parsed;
}

export const load: PageServerLoad = async (event) => {
  const accessToken = event.locals.accessToken;
  if (accessToken === null) {
    redirect(303, '/login?returnUrl=' + encodeURIComponent(event.url.pathname));
  }

  const result = await fetchJson(event.fetch, ticketUrl(event.params.id), accessToken);

  if (result.status === 404) {
    error(404);
  }
  if (result.status === 403) {
    error(403);
  }

  const ticket = asTicket(result.data);
  if (ticket === null) {
    error(404);
  }

  return { ticket };
};

export const actions: Actions = {
  default: async (event: RequestEvent) => {
    const accessToken = event.locals.accessToken;
    if (accessToken === null) {
      redirect(303, '/login?returnUrl=' + encodeURIComponent(event.url.pathname));
    }

    const id = event.params.id;
    const parsedId = Number.parseInt(id, 10);
    if (Number.isNaN(parsedId)) {
      error(404);
    }

    const form = await event.request.formData();
    const assigneeId = readString(form, 'assigneeId');
    const values = {
      // Id must equal the route id (the API rejects a mismatch).
      Id: parsedId,
      Title: readString(form, 'title'),
      Description: readString(form, 'description'),
      Status: readInt(form, 'status', 0),
      Priority: readInt(form, 'priority', 1),
      ProjectId: readString(form, 'projectId'),
      TeamId: readString(form, 'teamId'),
      ReporterId: readString(form, 'reporterId'),
      AssigneeId: assigneeId.length > 0 ? assigneeId : null,
    };

    const result = await fetchJson(event.fetch, ticketUrl(id), accessToken, {
      method: 'PUT',
      body: JSON.stringify(values),
    });

    if (result.status === 204) {
      redirect(303, '/tickets/' + id);
    }
    if (result.status === 404) {
      error(404);
    }
    if (result.status === 422) {
      return fail(422, { errors: parseValidationErrors(result.data), values });
    }

    return fail(result.status === 0 ? 400 : result.status, {
      errors: { '': ['Unable to save the ticket. Please try again.'] },
      values,
    });
  },
};
