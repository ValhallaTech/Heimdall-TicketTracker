/**
 * Typed client helpers for the Heimdall ticket API (Phase 6.4).
 *
 * The backend (`ApiTicketsEndpoints` / `TicketDto`) serialises **PascalCase**
 * JSON and **numeric** enums, so the interfaces and enums below mirror that
 * wire format exactly rather than re-casing it. All parsing goes through
 * `unknown` and is narrowed by hand (strict TS, no `any`).
 *
 * These helpers are import-safe from both server (`+page.server.ts`) and client
 * code, but the API itself must only ever be called from server load functions
 * / actions with the bearer token from `event.locals.accessToken`.
 */

/**
 * Lifecycle status of a ticket. Numeric values mirror
 * `Heimdall.Core.Models.TicketStatus`.
 */
export enum TicketStatus {
  Open = 0,
  InProgress = 1,
  Resolved = 2,
  Closed = 3,
}

/**
 * Priority of a ticket. Numeric values mirror
 * `Heimdall.Core.Models.TicketPriority`.
 */
export enum TicketPriority {
  Low = 0,
  Medium = 1,
  High = 2,
  Critical = 3,
}

/** Human-readable labels for {@link TicketStatus}. */
export const ticketStatusLabels: Record<TicketStatus, string> = {
  [TicketStatus.Open]: 'Open',
  [TicketStatus.InProgress]: 'In Progress',
  [TicketStatus.Resolved]: 'Resolved',
  [TicketStatus.Closed]: 'Closed',
};

/** Human-readable labels for {@link TicketPriority}. */
export const ticketPriorityLabels: Record<TicketPriority, string> = {
  [TicketPriority.Low]: 'Low',
  [TicketPriority.Medium]: 'Medium',
  [TicketPriority.High]: 'High',
  [TicketPriority.Critical]: 'Critical',
};

/**
 * Tailwind badge classes per status. Mirrors the colour intent of the Razor
 * `StatusBadgeClass` (primary / info / success / secondary) using the project's
 * design tokens.
 */
export const ticketStatusBadgeClass: Record<TicketStatus, string> = {
  [TicketStatus.Open]: 'bg-primary text-primary-foreground',
  [TicketStatus.InProgress]: 'bg-sky-600 text-white',
  [TicketStatus.Resolved]: 'bg-emerald-600 text-white',
  [TicketStatus.Closed]: 'bg-secondary text-secondary-foreground',
};

/**
 * Tailwind badge classes per priority. Mirrors the colour intent of the Razor
 * `PriorityBadgeClass` (secondary / info / warning / danger).
 */
export const ticketPriorityBadgeClass: Record<TicketPriority, string> = {
  [TicketPriority.Low]: 'bg-secondary text-secondary-foreground',
  [TicketPriority.Medium]: 'bg-sky-600 text-white',
  [TicketPriority.High]: 'bg-amber-500 text-black',
  [TicketPriority.Critical]: 'bg-destructive text-white',
};

/**
 * A ticket as returned by `GET /api/v1/tickets[/{id}]`. PascalCase field names
 * and numeric enums match `TicketDto` on the wire.
 */
export interface Ticket {
  Id: number;
  Title: string;
  Description: string;
  Status: TicketStatus;
  Priority: TicketPriority;
  ProjectId: string;
  TeamId: string;
  ReporterId: string;
  AssigneeId: string | null;
  DateCreated: string;
  DateUpdated: string;
}

/**
 * A single page of results, mirroring `PagedResult<T>` (PascalCase). `TotalPages`
 * is a computed/serialised property on the backend.
 */
export interface PagedResult<T> {
  Items: T[];
  TotalCount: number;
  Page: number;
  PageSize: number;
  TotalPages: number;
}

/** Result of an authenticated JSON call: status plus the (unparsed) body. */
export interface ApiResult {
  /** HTTP status code. */
  status: number;
  /** Whether the response was 2xx. */
  ok: boolean;
  /** Parsed JSON body as `unknown` (callers narrow) — `null` if absent/malformed. */
  data: unknown;
}

/** Minimal `fetch` surface so the helper is callable with `event.fetch`. */
export type FetchLike = (input: string, init?: RequestInit) => Promise<Response>;

/**
 * Perform an authenticated JSON request against the ticket API.
 *
 * Attaches the bearer token, negotiates JSON, and returns the status alongside
 * the parsed body (as `unknown`). The body is parsed defensively: a 204 / empty
 * / non-JSON response yields `data: null` rather than throwing, so callers can
 * branch on `status` (e.g. 404 → `error(404)`) without a try/catch.
 */
export async function fetchJson(
  fetchFn: FetchLike,
  url: string,
  accessToken: string,
  init?: RequestInit,
): Promise<ApiResult> {
  const headers = new Headers(init?.headers);
  headers.set('authorization', `Bearer ${accessToken}`);
  headers.set('accept', 'application/json');
  if (init?.body !== undefined && !headers.has('content-type')) {
    headers.set('content-type', 'application/json');
  }

  const response = await fetchFn(url, { ...init, headers });

  let data: unknown;
  try {
    const text = await response.text();
    data = text.length > 0 ? (JSON.parse(text) as unknown) : null;
  } catch {
    data = null;
  }

  return { status: response.status, ok: response.ok, data };
}

/** Type guard: is the value a plain (non-null) object? */
function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

/**
 * Narrow an `unknown` JSON value to a {@link Ticket}. Returns `null` when the
 * shape does not match, so callers never have to trust the wire blindly.
 */
export function asTicket(value: unknown): Ticket | null {
  if (!isRecord(value)) {
    return null;
  }
  const {
    Id,
    Title,
    Description,
    Status,
    Priority,
    ProjectId,
    TeamId,
    ReporterId,
    AssigneeId,
    DateCreated,
    DateUpdated,
  } = value;

  if (
    typeof Id !== 'number' ||
    typeof Title !== 'string' ||
    typeof Description !== 'string' ||
    typeof Status !== 'number' ||
    typeof Priority !== 'number' ||
    typeof ProjectId !== 'string' ||
    typeof TeamId !== 'string' ||
    typeof ReporterId !== 'string' ||
    typeof DateCreated !== 'string' ||
    typeof DateUpdated !== 'string'
  ) {
    return null;
  }

  return {
    Id,
    Title,
    Description,
    Status,
    Priority,
    ProjectId,
    TeamId,
    ReporterId,
    AssigneeId: typeof AssigneeId === 'string' ? AssigneeId : null,
    DateCreated,
    DateUpdated,
  };
}

/**
 * Narrow an `unknown` JSON value to a {@link PagedResult} of {@link Ticket}.
 * Non-ticket items are dropped; a non-matching envelope yields an empty page so
 * the UI degrades gracefully rather than throwing.
 */
export function asTicketPage(value: unknown): PagedResult<Ticket> {
  const empty: PagedResult<Ticket> = {
    Items: [],
    TotalCount: 0,
    Page: 1,
    PageSize: 0,
    TotalPages: 0,
  };

  if (!isRecord(value)) {
    return empty;
  }

  const items = Array.isArray(value.Items)
    ? value.Items.map(asTicket).filter((t): t is Ticket => t !== null)
    : [];

  return {
    Items: items,
    TotalCount: typeof value.TotalCount === 'number' ? value.TotalCount : items.length,
    Page: typeof value.Page === 'number' ? value.Page : 1,
    PageSize: typeof value.PageSize === 'number' ? value.PageSize : items.length,
    TotalPages: typeof value.TotalPages === 'number' ? value.TotalPages : 0,
  };
}

/**
 * Extract the field-keyed validation errors from an RFC 9457
 * `problem+json` body (`HttpValidationProblemDetails`), as returned on 422.
 * Returns an empty map when the body has no usable `errors` object.
 */
export function parseValidationErrors(value: unknown): Record<string, string[]> {
  if (!isRecord(value) || !isRecord(value.errors)) {
    return {};
  }

  const result: Record<string, string[]> = {};
  for (const [field, messages] of Object.entries(value.errors)) {
    if (Array.isArray(messages)) {
      result[field] = messages.filter((m): m is string => typeof m === 'string');
    }
  }
  return result;
}
