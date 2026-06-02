<script lang="ts">
  /**
   * New ticket form — ports `NewTicket.razor` (Phase 6.4).
   *
   * Posts to the co-located default action, progressively enhanced with
   * `use:enhance`. Status / Priority are populated from the label maps; on a 422
   * the action returns field-keyed errors (`form.errors`) and the submitted
   * `form.values`, both surfaced below the relevant fields.
   *
   * Deviation: Team / Project are raw id inputs (no lookup API yet) and Reporter
   * defaults to the signed-in user server-side — see the loader's NOTE.
   */
  import { enhance } from '$app/forms';
  import { resolve } from '$app/paths';
  import {
    TicketStatus,
    TicketPriority,
    ticketStatusLabels,
    ticketPriorityLabels,
  } from '$lib/api/tickets';
  import type { ActionData } from './$types';

  let { form }: { form: ActionData } = $props();

  const statusOptions = [
    TicketStatus.Open,
    TicketStatus.InProgress,
    TicketStatus.Resolved,
    TicketStatus.Closed,
  ];
  const priorityOptions = [
    TicketPriority.Low,
    TicketPriority.Medium,
    TicketPriority.High,
    TicketPriority.Critical,
  ];

  /** First error message for a field, or '' when none. */
  function fieldError(field: string): string {
    return form?.errors?.[field]?.[0] ?? '';
  }

  const formError = $derived(fieldError(''));
</script>

<svelte:head>
  <title>New Ticket — Heimdall TicketTracker</title>
</svelte:head>

<section class="mx-auto w-full max-w-2xl px-4 py-8">
  <div class="mb-4">
    <a class="text-primary text-sm hover:underline" href={resolve('/tickets')}>← Back to tickets</a>
  </div>

  <div class="border-border bg-card text-card-foreground rounded-xl border shadow-sm">
    <div class="border-border border-b px-6 py-4">
      <h1 class="text-xl font-semibold">New Ticket</h1>
    </div>

    <form method="POST" use:enhance class="space-y-5 p-6" data-testid="ticket-new-form">
      {#if formError}
        <div
          class="border-destructive/40 bg-destructive/10 rounded-lg border px-4 py-3 text-sm"
          role="alert"
        >
          {formError}
        </div>
      {/if}

      <div>
        <label for="title" class="mb-1 block text-sm font-medium">Title</label>
        <input
          id="title"
          name="title"
          required
          value={form?.values?.Title ?? ''}
          placeholder="Short summary of the issue"
          aria-invalid={fieldError('Title') ? 'true' : undefined}
          class="border-input bg-background w-full rounded-lg border px-3 py-2"
        />
        {#if fieldError('Title')}
          <p class="text-destructive mt-1 text-sm">{fieldError('Title')}</p>
        {/if}
      </div>

      <div>
        <label for="description" class="mb-1 block text-sm font-medium">Description</label>
        <textarea
          id="description"
          name="description"
          rows="5"
          required
          placeholder="Detailed description, steps to reproduce, expected behaviour, etc."
          aria-invalid={fieldError('Description') ? 'true' : undefined}
          class="border-input bg-background w-full rounded-lg border px-3 py-2"
          >{form?.values?.Description ?? ''}</textarea
        >
        {#if fieldError('Description')}
          <p class="text-destructive mt-1 text-sm">{fieldError('Description')}</p>
        {/if}
      </div>

      <div class="grid gap-4 sm:grid-cols-2">
        <div>
          <label for="status" class="mb-1 block text-sm font-medium">Status</label>
          <select
            id="status"
            name="status"
            class="border-input bg-background w-full rounded-lg border px-3 py-2"
          >
            {#each statusOptions as status (status)}
              <option
                value={status}
                selected={status === (form?.values?.Status ?? TicketStatus.Open)}
              >
                {ticketStatusLabels[status]}
              </option>
            {/each}
          </select>
        </div>
        <div>
          <label for="priority" class="mb-1 block text-sm font-medium">Priority</label>
          <select
            id="priority"
            name="priority"
            class="border-input bg-background w-full rounded-lg border px-3 py-2"
          >
            {#each priorityOptions as priority (priority)}
              <option
                value={priority}
                selected={priority === (form?.values?.Priority ?? TicketPriority.Medium)}
              >
                {ticketPriorityLabels[priority]}
              </option>
            {/each}
          </select>
        </div>
      </div>

      <div class="grid gap-4 sm:grid-cols-2">
        <div>
          <label for="teamId" class="mb-1 block text-sm font-medium">Team</label>
          <input
            id="teamId"
            name="teamId"
            required
            value={form?.values?.TeamId ?? ''}
            placeholder="Team id (GUID)"
            aria-invalid={fieldError('TeamId') ? 'true' : undefined}
            class="border-input bg-background w-full rounded-lg border px-3 py-2"
          />
          {#if fieldError('TeamId')}
            <p class="text-destructive mt-1 text-sm">{fieldError('TeamId')}</p>
          {/if}
        </div>
        <div>
          <label for="projectId" class="mb-1 block text-sm font-medium">Project</label>
          <input
            id="projectId"
            name="projectId"
            required
            value={form?.values?.ProjectId ?? ''}
            placeholder="Project id (GUID)"
            aria-invalid={fieldError('ProjectId') ? 'true' : undefined}
            class="border-input bg-background w-full rounded-lg border px-3 py-2"
          />
          {#if fieldError('ProjectId')}
            <p class="text-destructive mt-1 text-sm">{fieldError('ProjectId')}</p>
          {/if}
        </div>
      </div>

      {#if fieldError('ReporterId')}
        <p class="text-destructive text-sm">{fieldError('ReporterId')}</p>
      {/if}

      <div class="flex gap-3 pt-2">
        <button
          type="submit"
          class="bg-primary text-primary-foreground hover:bg-primary/90 rounded-lg px-5 py-2 font-medium transition-colors"
        >
          Create Ticket
        </button>
        <a
          href={resolve('/tickets')}
          class="border-input hover:bg-accent rounded-lg border px-5 py-2 font-medium transition-colors"
        >
          Cancel
        </a>
      </div>
    </form>
  </div>
</section>
