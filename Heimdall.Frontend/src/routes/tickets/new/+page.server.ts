/**
 * New-ticket form action — ports the create path from `NewTicket.razor`
 * (Phase 6.4).
 *
 * Authenticated: requires `event.locals.accessToken`; redirects to `/login`
 * otherwise. Reads the form fields, sets `ReporterId` to the current user
 * (mirroring the Razor default of the signed-in user as reporter), and POSTs
 * PascalCase JSON to `POST /api/v1/tickets`. On 201 it redirects to the created
 * ticket; on 422 it returns the field-keyed validation errors plus the
 * submitted values so the form can re-render them.
 *
 * Deviation from parity: `NewTicket.razor` populates Team / Project / Reporter /
 * Assignee from repositories that the JSON ticket API does not expose. Until a
 * lookup API lands, Team / Project ids are entered directly and Reporter
 * defaults to the signed-in user. // NOTE: replace the raw id inputs with
 * populated selects once a teams/projects lookup API exists.
 */
import { env } from '$env/dynamic/private';
import { fail, redirect } from '@sveltejs/kit';
import { asTicket, fetchJson, parseValidationErrors } from '$lib/api/tickets';
import type { Actions, RequestEvent } from './$types';

const DEFAULT_INTERNAL_API_ORIGIN = 'http://127.0.0.1:5000';

function internalApiOrigin(): string {
  return env.INTERNAL_API_ORIGIN ?? DEFAULT_INTERNAL_API_ORIGIN;
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

export const actions: Actions = {
  default: async (event: RequestEvent) => {
    const accessToken = event.locals.accessToken;
    if (accessToken === null) {
      redirect(303, '/login?returnUrl=' + encodeURIComponent(event.url.pathname));
    }

    const form = await event.request.formData();
    const values = {
      Title: readString(form, 'title'),
      Description: readString(form, 'description'),
      Status: readInt(form, 'status', 0),
      Priority: readInt(form, 'priority', 1),
      ProjectId: readString(form, 'projectId'),
      TeamId: readString(form, 'teamId'),
      // Reporter defaults to the signed-in user, mirroring NewTicket.razor.
      ReporterId: event.locals.user?.id ?? '',
      AssigneeId: null,
    };

    const result = await fetchJson(
      event.fetch,
      `${internalApiOrigin()}/api/v1/tickets`,
      accessToken,
      { method: 'POST', body: JSON.stringify(values) },
    );

    if (result.status === 201) {
      const created = asTicket(result.data);
      if (created !== null) {
        redirect(303, '/tickets/' + created.Id);
      }
      // Created but unparseable body — fall back to the list.
      redirect(303, '/tickets');
    }

    if (result.status === 422) {
      return fail(422, { errors: parseValidationErrors(result.data), values });
    }

    return fail(result.status === 0 ? 400 : result.status, {
      errors: { '': ['Unable to create the ticket. Please try again.'] },
      values,
    });
  },
};
