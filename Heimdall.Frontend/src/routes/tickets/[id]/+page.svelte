<script lang="ts">
  /**
   * Ticket edit form — ports `TicketEdit.razor` (Phase 6.4).
   *
   * Prefilled from the loaded ticket (`data.ticket`); after a failed submit the
   * action returns `form.values` (which take precedence so the user's edits are
   * preserved) and `form.errors` (surfaced per field). Posts to the co-located
   * default action, progressively enhanced with `use:enhance`.
   *
   * Deviation: Team / Project / Reporter / Assignee are raw id fields (no lookup
   * API yet) — see the loader NOTE.
   */
  import { enhance } from '$app/forms';
  import { resolve } from '$app/paths';
  import {
    TicketStatus,
    TicketPriority,
    ticketStatusLabels,
    ticketPriorityLabels,
  } from '$lib/api/tickets';
  import type { ActionData, PageData } from './$types';

  let { data, form }: { data: PageData; form: ActionData } = $props();

  /** Effective values: the user's last submission wins over the loaded ticket. */
  const values = $derived(form?.values ?? data.ticket);

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

  function fieldError(field: string): string {
    return form?.errors?.[field]?.[0] ?? '';
  }

  const formError = $derived(fieldError(''));
</script>

<svelte:head>
  <title>Edit Ticket — Heimdall TicketTracker</title>
</svelte:head>

<section class="mx-auto w-full max-w-2xl px-4 py-8">
  <div class="mb-4">
    <a class="text-primary text-sm hover:underline" href={resolve('/tickets')}>← Back to tickets</a>
  </div>

  <div class="border-border bg-card text-card-foreground rounded-xl border shadow-sm">
    <div class="border-border border-b px-6 py-4">
      <h1 class="text-xl font-semibold">Edit Ticket</h1>
    </div>

    <form method="POST" use:enhance class="space-y-5 p-6" data-testid="ticket-edit-form">
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
          value={values.Title}
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
          aria-invalid={fieldError('Description') ? 'true' : undefined}
          class="border-input bg-background w-full rounded-lg border px-3 py-2"
          >{values.Description}</textarea
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
              <option value={status} selected={status === values.Status}>
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
              <option value={priority} selected={priority === values.Priority}>
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
            value={values.TeamId}
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
            value={values.ProjectId}
            aria-invalid={fieldError('ProjectId') ? 'true' : undefined}
            class="border-input bg-background w-full rounded-lg border px-3 py-2"
          />
          {#if fieldError('ProjectId')}
            <p class="text-destructive mt-1 text-sm">{fieldError('ProjectId')}</p>
          {/if}
        </div>
      </div>

      <div class="grid gap-4 sm:grid-cols-2">
        <div>
          <label for="reporterId" class="mb-1 block text-sm font-medium">Reporter</label>
          <input
            id="reporterId"
            name="reporterId"
            required
            value={values.ReporterId}
            aria-invalid={fieldError('ReporterId') ? 'true' : undefined}
            class="border-input bg-background w-full rounded-lg border px-3 py-2"
          />
          {#if fieldError('ReporterId')}
            <p class="text-destructive mt-1 text-sm">{fieldError('ReporterId')}</p>
          {/if}
        </div>
        <div>
          <label for="assigneeId" class="mb-1 block text-sm font-medium">
            Assignee <span class="text-muted-foreground font-normal">(optional)</span>
          </label>
          <input
            id="assigneeId"
            name="assigneeId"
            value={values.AssigneeId ?? ''}
            placeholder="Unassigned"
            class="border-input bg-background w-full rounded-lg border px-3 py-2"
          />
        </div>
      </div>

      <div class="flex gap-3 pt-2">
        <button
          type="submit"
          class="bg-primary text-primary-foreground hover:bg-primary/90 rounded-lg px-5 py-2 font-medium transition-colors"
        >
          Save Changes
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
