<script lang="ts">
  /**
   * Shared, presentational ticket table — ports `Teams/TicketQueueTable.razor`
   * (Phase 6.4). Renders a list of {@link Ticket} rows with a title link to the
   * ticket editor, status / priority badges (via the label + badge maps), the
   * assignee, and the created / updated dates. Handles the empty state.
   *
   * Purely presentational: NO data fetching and no permission logic (the Razor
   * original's per-row Route / Claim / Assign actions depend on server-side
   * permission fan-out that the JSON ticket API does not expose yet — see the
   * deviation note in the loaders). Used by both the tickets list and the team
   * queue. Tailwind utilities only; no Font Awesome.
   */
  import { resolve } from '$app/paths';
  import {
    type Ticket,
    ticketStatusLabels,
    ticketPriorityLabels,
    ticketStatusBadgeClass,
    ticketPriorityBadgeClass,
  } from '$lib/api/tickets';

  interface TicketQueueTableProps {
    /** The tickets to render. */
    tickets: Ticket[];
    /** Copy shown when there are no tickets. */
    emptyMessage?: string;
  }

  let { tickets, emptyMessage = 'No tickets in this queue.' }: TicketQueueTableProps = $props();

  /** Format an ISO timestamp as e.g. "Jan 5, 2025"; fall back to the raw value. */
  function formatDate(iso: string): string {
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) {
      return iso;
    }
    return date.toLocaleDateString('en-US', { year: 'numeric', month: 'short', day: 'numeric' });
  }
</script>

<div
  class="border-border overflow-x-auto rounded-xl border shadow-sm"
  data-testid="ticket-queue-table"
>
  <table class="w-full border-collapse text-left text-sm">
    <thead class="border-border text-muted-foreground border-b">
      <tr>
        <th scope="col" class="px-4 py-3 font-semibold">Title</th>
        <th scope="col" class="px-4 py-3 font-semibold">Status</th>
        <th scope="col" class="px-4 py-3 font-semibold">Priority</th>
        <th scope="col" class="px-4 py-3 font-semibold">Assignee</th>
        <th scope="col" class="px-4 py-3 font-semibold whitespace-nowrap">Created</th>
      </tr>
    </thead>
    <tbody>
      {#if tickets.length === 0}
        <tr>
          <td colspan="5" class="text-muted-foreground px-4 py-8 text-center"> {emptyMessage} </td>
        </tr>
      {:else}
        {#each tickets as ticket (ticket.Id)}
          <tr class="border-border hover:bg-accent/40 border-b last:border-0">
            <td class="px-4 py-3 font-medium">
              <a
                class="text-primary hover:underline"
                href={resolve('/tickets/[id]', { id: String(ticket.Id) })}
              >
                {ticket.Title}
              </a>
            </td>
            <td class="px-4 py-3">
              <span
                class="inline-block rounded px-2 py-0.5 text-xs font-medium {ticketStatusBadgeClass[
                  ticket.Status
                ]}"
              >
                {ticketStatusLabels[ticket.Status]}
              </span>
            </td>
            <td class="px-4 py-3">
              <span
                class="inline-block rounded px-2 py-0.5 text-xs font-medium {ticketPriorityBadgeClass[
                  ticket.Priority
                ]}"
              >
                {ticketPriorityLabels[ticket.Priority]}
              </span>
            </td>
            <td class="text-muted-foreground px-4 py-3">
              {ticket.AssigneeId ?? '—'}
            </td>
            <td class="text-muted-foreground px-4 py-3 whitespace-nowrap">
              {formatDate(ticket.DateCreated)}
            </td>
          </tr>
        {/each}
      {/if}
    </tbody>
  </table>
</div>
