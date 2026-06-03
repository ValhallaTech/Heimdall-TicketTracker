/**
 * Unit spec for the typed ticket API helpers (`lib/api/tickets.ts`, Phase 6.4).
 *
 * Pure functions only — no SvelteKit / DOM. `fetchJson` is exercised against a
 * fake `FetchLike` returning real `Response` objects so header negotiation and
 * defensive body parsing (204 / empty / non-JSON) are covered without a network.
 * The narrowing helpers (`asTicket`, `asTicketPage`, `parseValidationErrors`)
 * are tested on both well-formed and malformed `unknown` input so every
 * narrowing-failure branch (and thus the 80% branch gate) is hit.
 */
import { describe, expect, it, vi } from 'vitest';

import {
  type FetchLike,
  type Ticket,
  TicketStatus,
  TicketPriority,
  ticketStatusLabels,
  ticketPriorityLabels,
  ticketStatusBadgeClass,
  ticketPriorityBadgeClass,
  fetchJson,
  asTicket,
  asTicketPage,
  parseValidationErrors,
} from './tickets';

/** A fully-populated, well-formed ticket for the happy-path narrowing tests. */
function makeWireTicket(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    Id: 7,
    Title: 'Bifrost is down',
    Description: 'The rainbow bridge will not render.',
    Status: TicketStatus.InProgress,
    Priority: TicketPriority.High,
    ProjectId: 'project-1',
    TeamId: 'team-1',
    ReporterId: 'reporter-1',
    AssigneeId: 'assignee-1',
    DateCreated: '2025-01-05T10:00:00Z',
    DateUpdated: '2025-01-06T10:00:00Z',
    ...overrides,
  };
}

/** Build a `FetchLike` that resolves with a single canned `Response`. */
function fetchReturning(response: Response): { fn: FetchLike; mock: ReturnType<typeof vi.fn> } {
  const mock = vi.fn(async () => response);
  return { fn: mock as unknown as FetchLike, mock };
}

describe('lib/api/tickets', () => {
  describe('label & badge maps', () => {
    const statuses = [
      TicketStatus.Open,
      TicketStatus.InProgress,
      TicketStatus.Resolved,
      TicketStatus.Closed,
    ];
    const priorities = [
      TicketPriority.Low,
      TicketPriority.Medium,
      TicketPriority.High,
      TicketPriority.Critical,
    ];

    it('maps every TicketStatus to a non-empty label', () => {
      for (const status of statuses) {
        expect(ticketStatusLabels[status]).toBeTruthy();
        expect(typeof ticketStatusLabels[status]).toBe('string');
      }
      expect(ticketStatusLabels[TicketStatus.InProgress]).toBe('In Progress');
    });

    it('maps every TicketPriority to a non-empty label', () => {
      for (const priority of priorities) {
        expect(ticketPriorityLabels[priority]).toBeTruthy();
        expect(typeof ticketPriorityLabels[priority]).toBe('string');
      }
      expect(ticketPriorityLabels[TicketPriority.Critical]).toBe('Critical');
    });

    it('maps every TicketStatus/TicketPriority to a badge class', () => {
      for (const status of statuses) {
        expect(ticketStatusBadgeClass[status]).toBeTruthy();
      }
      for (const priority of priorities) {
        expect(ticketPriorityBadgeClass[priority]).toBeTruthy();
      }
    });
  });

  describe('fetchJson', () => {
    it('sets the Authorization and Accept headers from the access token', async () => {
      const { fn, mock } = fetchReturning(new Response('{}', { status: 200 }));

      await fetchJson(fn, 'https://api.test/tickets', 'token-abc');

      expect(mock).toHaveBeenCalledTimes(1);
      const [url, init] = mock.mock.calls[0] as [string, RequestInit];
      expect(url).toBe('https://api.test/tickets');
      const headers = new Headers(init.headers);
      expect(headers.get('authorization')).toBe('Bearer ' + 'token-abc');
      expect(headers.get('accept')).toBe('application/json');
    });

    it('negotiates content-type only when a body is sent', async () => {
      const { fn, mock } = fetchReturning(new Response('{}', { status: 200 }));

      await fetchJson(fn, 'https://api.test/tickets', 'token-abc', {
        method: 'POST',
        body: JSON.stringify({ Title: 'x' }),
      });

      const [, init] = mock.mock.calls[0] as [string, RequestInit];
      const headers = new Headers(init.headers);
      expect(headers.get('content-type')).toBe('application/json');
    });

    it('does not override an explicit content-type when a body is sent', async () => {
      const { fn, mock } = fetchReturning(new Response('{}', { status: 200 }));

      await fetchJson(fn, 'https://api.test/tickets', 'token-abc', {
        method: 'POST',
        body: 'raw',
        headers: { 'content-type': 'text/plain' },
      });

      const [, init] = mock.mock.calls[0] as [string, RequestInit];
      expect(new Headers(init.headers).get('content-type')).toBe('text/plain');
    });

    it('omits content-type when there is no body', async () => {
      const { fn, mock } = fetchReturning(new Response('{}', { status: 200 }));

      await fetchJson(fn, 'https://api.test/tickets', 'token-abc');

      const [, init] = mock.mock.calls[0] as [string, RequestInit];
      expect(new Headers(init.headers).has('content-type')).toBe(false);
    });

    it('returns { status, ok, data } with parsed JSON on a 2xx body', async () => {
      const body = JSON.stringify({ Id: 1, Title: 'parsed' });
      const { fn } = fetchReturning(
        new Response(body, { status: 200, headers: { 'content-type': 'application/json' } }),
      );

      const result = await fetchJson(fn, 'https://api.test/tickets/1', 'token-abc');

      expect(result.status).toBe(200);
      expect(result.ok).toBe(true);
      expect(result.data).toEqual({ Id: 1, Title: 'parsed' });
    });

    it('returns data: null for a 204 / empty response without throwing', async () => {
      const { fn } = fetchReturning(new Response(null, { status: 204 }));

      const result = await fetchJson(fn, 'https://api.test/tickets/1', 'token-abc', {
        method: 'DELETE',
      });

      expect(result.status).toBe(204);
      expect(result.ok).toBe(true);
      expect(result.data).toBeNull();
    });

    it('returns data: null for a non-JSON body without throwing', async () => {
      const { fn } = fetchReturning(new Response('<html>not json</html>', { status: 200 }));

      const result = await fetchJson(fn, 'https://api.test/tickets', 'token-abc');

      expect(result.data).toBeNull();
    });

    it('surfaces non-2xx status codes (404) on the result', async () => {
      const { fn } = fetchReturning(new Response('{}', { status: 404 }));

      const result = await fetchJson(fn, 'https://api.test/tickets/999', 'token-abc');

      expect(result.status).toBe(404);
      expect(result.ok).toBe(false);
    });

    it('surfaces a 403 status on the result', async () => {
      const { fn } = fetchReturning(new Response('{}', { status: 403 }));

      const result = await fetchJson(fn, 'https://api.test/tickets/1', 'token-abc');

      expect(result.status).toBe(403);
      expect(result.ok).toBe(false);
    });
  });

  describe('asTicket', () => {
    it('narrows a well-formed PascalCase ticket object', () => {
      const ticket = asTicket(makeWireTicket());

      expect(ticket).not.toBeNull();
      expect(ticket?.Id).toBe(7);
      expect(ticket?.Title).toBe('Bifrost is down');
      expect(ticket?.AssigneeId).toBe('assignee-1');
    });

    it('returns null for a non-object value', () => {
      expect(asTicket(null)).toBeNull();
      expect(asTicket('string')).toBeNull();
      expect(asTicket(42)).toBeNull();
    });

    it('returns null when a required field is missing', () => {
      const { Title: _omitted, ...withoutTitle } = makeWireTicket();
      void _omitted;
      expect(asTicket(withoutTitle)).toBeNull();
    });

    it('returns null when a required field is mistyped', () => {
      expect(asTicket(makeWireTicket({ Id: '7' }))).toBeNull();
      expect(asTicket(makeWireTicket({ Status: 'open' }))).toBeNull();
      expect(asTicket(makeWireTicket({ Priority: null }))).toBeNull();
      expect(asTicket(makeWireTicket({ ProjectId: 1 }))).toBeNull();
      expect(asTicket(makeWireTicket({ TeamId: 1 }))).toBeNull();
      expect(asTicket(makeWireTicket({ ReporterId: 1 }))).toBeNull();
      expect(asTicket(makeWireTicket({ DateCreated: 0 }))).toBeNull();
      expect(asTicket(makeWireTicket({ DateUpdated: 0 }))).toBeNull();
      expect(asTicket(makeWireTicket({ Description: 0 }))).toBeNull();
    });

    it('treats a non-string AssigneeId as null (unassigned)', () => {
      expect(asTicket(makeWireTicket({ AssigneeId: null }))?.AssigneeId).toBeNull();
      expect(asTicket(makeWireTicket({ AssigneeId: 123 }))?.AssigneeId).toBeNull();
      expect(asTicket(makeWireTicket({ AssigneeId: undefined }))?.AssigneeId).toBeNull();
    });
  });

  describe('asTicketPage', () => {
    it('narrows a well-formed PagedResult<Ticket> envelope', () => {
      const page = asTicketPage({
        Items: [makeWireTicket({ Id: 1 }), makeWireTicket({ Id: 2 })],
        TotalCount: 2,
        Page: 3,
        PageSize: 10,
        TotalPages: 5,
      });

      expect(page.Items).toHaveLength(2);
      expect(page.Items.map((t: Ticket) => t.Id)).toEqual([1, 2]);
      expect(page.TotalCount).toBe(2);
      expect(page.Page).toBe(3);
      expect(page.PageSize).toBe(10);
      expect(page.TotalPages).toBe(5);
    });

    it('drops non-ticket items from Items', () => {
      const page = asTicketPage({
        Items: [makeWireTicket({ Id: 1 }), { not: 'a ticket' }, null, 42],
        TotalCount: 1,
      });

      expect(page.Items).toHaveLength(1);
      expect(page.Items[0].Id).toBe(1);
    });

    it('falls back to computed counts when envelope fields are missing/mistyped', () => {
      const page = asTicketPage({ Items: [makeWireTicket({ Id: 1 })] });

      expect(page.TotalCount).toBe(1);
      expect(page.Page).toBe(1);
      expect(page.PageSize).toBe(1);
      expect(page.TotalPages).toBe(0);
    });

    it('returns an empty page for a non-object envelope', () => {
      const page = asTicketPage('not an object');

      expect(page.Items).toEqual([]);
      expect(page.TotalCount).toBe(0);
      expect(page.Page).toBe(1);
      expect(page.PageSize).toBe(0);
      expect(page.TotalPages).toBe(0);
    });

    it('treats a non-array Items field as an empty list', () => {
      const page = asTicketPage({
        Items: 'nope',
        TotalCount: 9,
        Page: 2,
        PageSize: 4,
        TotalPages: 3,
      });

      expect(page.Items).toEqual([]);
      expect(page.TotalCount).toBe(9);
      expect(page.Page).toBe(2);
    });
  });

  describe('parseValidationErrors', () => {
    it('extracts a field-keyed errors map from a problem+json body', () => {
      const errors = parseValidationErrors({
        type: 'https://httpstatuses.io/422',
        errors: {
          Title: ['Title is required.'],
          Priority: ['Priority is invalid.', 'Out of range.'],
        },
      });

      expect(errors).toEqual({
        Title: ['Title is required.'],
        Priority: ['Priority is invalid.', 'Out of range.'],
      });
    });

    it('filters non-string messages out of a field array', () => {
      const errors = parseValidationErrors({ errors: { Title: ['ok', 42, null, 'also ok'] } });

      expect(errors.Title).toEqual(['ok', 'also ok']);
    });

    it('skips fields whose messages are not an array', () => {
      const errors = parseValidationErrors({ errors: { Title: 'not-an-array', Body: ['ok'] } });

      expect(errors).toEqual({ Body: ['ok'] });
    });

    it('returns an empty map when errors is absent', () => {
      expect(parseValidationErrors({ title: 'no errors here' })).toEqual({});
    });

    it('returns an empty map when the body is not an object', () => {
      expect(parseValidationErrors(null)).toEqual({});
      expect(parseValidationErrors('nope')).toEqual({});
    });

    it('returns an empty map when errors is not an object', () => {
      expect(parseValidationErrors({ errors: 'oops' })).toEqual({});
    });
  });
});
