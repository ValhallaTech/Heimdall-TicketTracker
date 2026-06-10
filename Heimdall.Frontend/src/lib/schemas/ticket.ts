/**
 * Shared Zod schemas for the ticket forms (Phase 6.5).
 *
 * These mirror the backend `TicketDto` validation rules so the browser can give
 * instant, inline feedback as the user types. Per
 * `docs/proposals/phase-6-adr.md` §5 (Option C — Hybrid), **Zod is a UX-only
 * convenience and is never the trust boundary**: the authoritative enforcement
 * lives in FluentValidation / the `TryValidate` guard on the .NET API
 * (`src/Heimdall.Web/Endpoints/ApiTicketsEndpoints.cs` +
 * `src/Heimdall.Core/Dtos/TicketDto.cs`). The two layers describe the same shape
 * and MUST be kept in sync — see the "Zod ↔ FluentValidation parity rule" in the
 * frontend README.
 *
 * Messages below are copied verbatim from the backend `TryValidate` so the
 * inline (Zod) and API (FluentValidation) errors read identically.
 *
 * Strict TS, no `any`. The numeric enums reuse {@link TicketStatus} /
 * {@link TicketPriority} from `$lib/api/tickets` as the source of the 0..3 range.
 */
import { z } from 'zod';
import { TicketStatus, TicketPriority } from '$lib/api/tickets';

/** Lowest / highest numeric value of the {@link TicketStatus} enum (0..3). */
const STATUS_MIN = TicketStatus.Open;
const STATUS_MAX = TicketStatus.Closed;

/** Lowest / highest numeric value of the {@link TicketPriority} enum (0..3). */
const PRIORITY_MIN = TicketPriority.Low;
const PRIORITY_MAX = TicketPriority.Critical;

/** `Title`: required, max 200 — messages mirror the backend `TryValidate`. */
const titleSchema = z
  .string()
  .min(1, 'Title is required.')
  .max(200, 'Title must be 200 characters or fewer.');

/** `Description`: required, max 4000 — messages mirror the backend `TryValidate`. */
const descriptionSchema = z
  .string()
  .min(1, 'Description is required.')
  .max(4000, 'Description must be 4000 characters or fewer.');

/** `Status`: integer in the {@link TicketStatus} range (0..3). */
const statusSchema = z
  .number()
  .int('Status must be a valid value.')
  .min(STATUS_MIN, 'Status must be a valid value.')
  .max(STATUS_MAX, 'Status must be a valid value.');

/** `Priority`: integer in the {@link TicketPriority} range (0..3). */
const prioritySchema = z
  .number()
  .int('Priority must be a valid value.')
  .min(PRIORITY_MIN, 'Priority must be a valid value.')
  .max(PRIORITY_MAX, 'Priority must be a valid value.');

/**
 * Build a required-GUID schema whose empty/invalid message mirrors the backend
 * `"{field} is required."` (the API rejects `Guid.Empty`).
 */
function requiredGuid(field: string): z.ZodString {
  return z.uuid(`${field} is required.`);
}

/**
 * New-ticket schema. `ReporterId` is supplied server-side (the signed-in user)
 * and `AssigneeId` defaults to unassigned, so neither is part of the create
 * form's editable shape — they are applied in the action.
 */
export const newTicketSchema = z.object({
  Title: titleSchema,
  Description: descriptionSchema,
  Status: statusSchema.default(TicketStatus.Open),
  Priority: prioritySchema.default(TicketPriority.Medium),
  ProjectId: requiredGuid('ProjectId'),
  TeamId: requiredGuid('TeamId'),
});

/**
 * Edit-ticket schema. Adds `Id` (the route id, forced server-side), the required
 * `ReporterId`, and the optional/nullable `AssigneeId` (an empty string from the
 * form is treated as "unassigned" → `null` when the payload is built).
 */
export const editTicketSchema = z.object({
  Id: z.number().int(),
  Title: titleSchema,
  Description: descriptionSchema,
  Status: statusSchema.default(TicketStatus.Open),
  Priority: prioritySchema.default(TicketPriority.Medium),
  ProjectId: requiredGuid('ProjectId'),
  TeamId: requiredGuid('TeamId'),
  ReporterId: requiredGuid('ReporterId'),
  // A valid GUID, or empty string / null for an unassigned ticket.
  AssigneeId: z
    .union([z.uuid('Assignee must be a valid user id.'), z.literal(''), z.null()])
    .optional(),
});

/** Inferred output type of {@link newTicketSchema}. */
export type NewTicketInput = z.infer<typeof newTicketSchema>;

/** Inferred output type of {@link editTicketSchema}. */
export type EditTicketInput = z.infer<typeof editTicketSchema>;
