/**
 * New-ticket loader + form action — ports the create path from `NewTicket.razor`
 * and retrofits Superforms + Zod (Phase 6.5).
 *
 * Validation is layered per `docs/proposals/phase-6-adr.md` §5 (Option C —
 * Hybrid): the {@link newTicketSchema} Zod schema drives instant inline UX
 * feedback in the browser, while the .NET API (`POST /api/v1/tickets`) remains
 * the **authoritative trust boundary** via FluentValidation. The browser result
 * is never trusted — the same PascalCase JSON is still POSTed to the API and any
 * API 422 is mapped back onto the Superforms `form` so server-only rules surface
 * inline too.
 *
 * Authenticated: requires `event.locals.accessToken`; redirects to `/login`
 * otherwise. `ReporterId` defaults to the signed-in user (mirroring the Razor
 * default) and `AssigneeId` is null on create.
 *
 * Deviation from parity: `NewTicket.razor` populates Team / Project / Reporter /
 * Assignee from repositories that the JSON ticket API does not expose. Until a
 * lookup API lands, Team / Project ids are entered directly and Reporter
 * defaults to the signed-in user. // NOTE: replace the raw id inputs with
 * populated selects once a teams/projects lookup API exists.
 */
import { env } from '$env/dynamic/private';
import { fail, redirect } from '@sveltejs/kit';
import { superValidate, setError } from 'sveltekit-superforms';
import { zod4 } from 'sveltekit-superforms/adapters';
import { asTicket, fetchJson, parseValidationErrors } from '$lib/api/tickets';
import { newTicketSchema } from '$lib/schemas/ticket';
import type { Actions, PageServerLoad, RequestEvent } from './$types';

const DEFAULT_INTERNAL_API_ORIGIN = 'http://127.0.0.1:5000';

function internalApiOrigin(): string {
  return env.INTERNAL_API_ORIGIN ?? DEFAULT_INTERNAL_API_ORIGIN;
}

/** Editable leaf fields of the new-ticket form (PascalCase, schema-aligned). */
const NEW_TICKET_FIELDS = [
  'Title',
  'Description',
  'Status',
  'Priority',
  'ProjectId',
  'TeamId',
] as const;
type NewTicketField = (typeof NEW_TICKET_FIELDS)[number];

function isNewTicketField(field: string): field is NewTicketField {
  return (NEW_TICKET_FIELDS as readonly string[]).includes(field);
}

export const load: PageServerLoad = async (event) => {
  if (event.locals.accessToken === null) {
    redirect(303, '/login?returnUrl=' + encodeURIComponent(event.url.pathname));
  }

  return { form: await superValidate(zod4(newTicketSchema)) };
};

export const actions: Actions = {
  default: async (event: RequestEvent) => {
    const accessToken = event.locals.accessToken;
    if (accessToken === null) {
      redirect(303, '/login?returnUrl=' + encodeURIComponent(event.url.pathname));
    }

    const form = await superValidate(event.request, zod4(newTicketSchema));
    if (!form.valid) {
      return fail(400, { form });
    }

    const payload = {
      Title: form.data.Title,
      Description: form.data.Description,
      Status: form.data.Status,
      Priority: form.data.Priority,
      ProjectId: form.data.ProjectId,
      TeamId: form.data.TeamId,
      // Reporter defaults to the signed-in user, mirroring NewTicket.razor.
      ReporterId: event.locals.user?.id ?? '',
      AssigneeId: null,
    };

    const result = await fetchJson(
      event.fetch,
      `${internalApiOrigin()}/api/v1/tickets`,
      accessToken,
      { method: 'POST', body: JSON.stringify(payload) },
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
      // Map the API's field-keyed validation errors onto the Superforms form so
      // server-authoritative rules surface inline. Unknown / non-form fields
      // (e.g. ReporterId, which is server-supplied) fall back to the form level.
      for (const [field, messages] of Object.entries(parseValidationErrors(result.data))) {
        if (isNewTicketField(field)) {
          setError(form, field, messages);
        } else {
          setError(form, messages);
        }
      }
      return fail(422, { form });
    }

    setError(form, 'Unable to create the ticket. Please try again.');
    return fail(result.status === 0 ? 400 : result.status, { form });
  },
};
