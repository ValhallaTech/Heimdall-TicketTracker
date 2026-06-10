/**
 * Ticket edit loader + update action — ports `TicketEdit.razor` and retrofits
 * Superforms + Zod (Phase 6.5).
 *
 * Validation is layered per `docs/proposals/phase-6-adr.md` §5 (Option C —
 * Hybrid): {@link editTicketSchema} drives instant inline UX validation in the
 * browser, while the .NET API (`PUT /api/v1/tickets/{id}`) is the
 * **authoritative trust boundary** via FluentValidation. The loader fetches the
 * ticket and seeds the Superforms `form`; the action re-validates, forces `Id`
 * to the route id, PUTs the PascalCase JSON, and maps API status codes
 * (204 → redirect, 404 → `error(404)`, 422 → inline field errors).
 *
 * Authenticated: requires `event.locals.accessToken`; redirects to `/login`
 * otherwise. The loader maps 404 → `error(404)`, 403 → `error(403)`.
 *
 * Deviation: Team / Project / Reporter / Assignee are edited as raw id fields
 * (no lookup API yet) — see the new-ticket loader NOTE.
 */
import { env } from '$env/dynamic/private';
import { error, fail, redirect } from '@sveltejs/kit';
import { superValidate, setError } from 'sveltekit-superforms';
import { zod4 } from 'sveltekit-superforms/adapters';
import { asTicket, fetchJson, parseValidationErrors } from '$lib/api/tickets';
import { editTicketSchema } from '$lib/schemas/ticket';
import type { Actions, PageServerLoad, RequestEvent } from './$types';

const DEFAULT_INTERNAL_API_ORIGIN = 'http://127.0.0.1:5000';

function internalApiOrigin(): string {
  return env.INTERNAL_API_ORIGIN ?? DEFAULT_INTERNAL_API_ORIGIN;
}

function ticketUrl(id: string): string {
  return `${internalApiOrigin()}/api/v1/tickets/${encodeURIComponent(id)}`;
}

/** Editable leaf fields of the edit-ticket form (PascalCase, schema-aligned). */
const EDIT_TICKET_FIELDS = [
  'Id',
  'Title',
  'Description',
  'Status',
  'Priority',
  'ProjectId',
  'TeamId',
  'ReporterId',
  'AssigneeId',
] as const;
type EditTicketField = (typeof EDIT_TICKET_FIELDS)[number];

function isEditTicketField(field: string): field is EditTicketField {
  return (EDIT_TICKET_FIELDS as readonly string[]).includes(field);
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

  // Seed the Superforms form from the fetched ticket (extra fields like
  // DateCreated / DateUpdated are ignored by the schema).
  const form = await superValidate(
    {
      Id: ticket.Id,
      Title: ticket.Title,
      Description: ticket.Description,
      Status: ticket.Status,
      Priority: ticket.Priority,
      ProjectId: ticket.ProjectId,
      TeamId: ticket.TeamId,
      ReporterId: ticket.ReporterId,
      AssigneeId: ticket.AssigneeId,
    },
    zod4(editTicketSchema),
  );

  return { ticket, form };
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

    const form = await superValidate(event.request, zod4(editTicketSchema));
    // Id must equal the route id (the API rejects a mismatch).
    form.data.Id = parsedId;

    if (!form.valid) {
      return fail(400, { form });
    }

    const assigneeId = form.data.AssigneeId;
    const payload = {
      Id: parsedId,
      Title: form.data.Title,
      Description: form.data.Description,
      Status: form.data.Status,
      Priority: form.data.Priority,
      ProjectId: form.data.ProjectId,
      TeamId: form.data.TeamId,
      ReporterId: form.data.ReporterId,
      // An empty string / missing assignee means "unassigned" → null.
      AssigneeId: assigneeId !== undefined && assigneeId !== null && assigneeId.length > 0
        ? assigneeId
        : null,
    };

    const result = await fetchJson(event.fetch, ticketUrl(id), accessToken, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });

    if (result.status === 204) {
      redirect(303, '/tickets/' + id);
    }
    if (result.status === 404) {
      error(404);
    }

    if (result.status === 422) {
      // Surface the API's authoritative validation errors inline.
      for (const [field, messages] of Object.entries(parseValidationErrors(result.data))) {
        if (isEditTicketField(field)) {
          setError(form, field, messages);
        } else {
          setError(form, messages);
        }
      }
      return fail(422, { form });
    }

    setError(form, 'Unable to save the ticket. Please try again.');
    return fail(result.status === 0 ? 400 : result.status, { form });
  },
};
