<script lang="ts">
  /**
   * New ticket form — ports `NewTicket.razor` and retrofits Superforms +
   * Formsnap + Zod (Phase 6.5).
   *
   * The {@link newTicketSchema} drives instant, inline Zod validation in the
   * browser (`zod4Client`) for UX; the co-located action re-validates and POSTs
   * to the .NET API, which is the authoritative trust boundary
   * (`docs/proposals/phase-6-adr.md` §5). Field-level errors come from Formsnap
   * `<FieldErrors />`; the form-level region (role="alert") surfaces both the
   * generic submit failure and any API errors mapped onto `$errors._errors`.
   *
   * Deviation: Team / Project are raw id inputs (no lookup API yet) and Reporter
   * defaults to the signed-in user server-side — see the loader's NOTE.
   */
  import { resolve } from '$app/paths';
  import { superForm } from 'sveltekit-superforms';
  import { zod4Client } from 'sveltekit-superforms/adapters';
  import { Field, Control, Label, FieldErrors } from 'formsnap';
  import {
    TicketStatus,
    TicketPriority,
    ticketStatusLabels,
    ticketPriorityLabels,
  } from '$lib/api/tickets';
  import { newTicketSchema } from '$lib/schemas/ticket';
  import type { PageData } from './$types';

  let { data }: { data: PageData } = $props();

  const form = superForm(data.form, {
    validators: zod4Client(newTicketSchema),
  });
  const { form: formData, errors, enhance } = form;

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

  /** Form-level (non field-specific) errors, e.g. a generic API failure. */
  const formErrors = $derived($errors._errors ?? []);
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
      {#if formErrors.length > 0}
        <div
          class="border-destructive/40 bg-destructive/10 rounded-lg border px-4 py-3 text-sm"
          role="alert"
        >
          {formErrors.join(' ')}
        </div>
      {/if}

      <Field {form} name="Title">
        <Control>
          {#snippet children({ props })}
            <Label class="mb-1 block text-sm font-medium">Title</Label>
            <input
              {...props}
              bind:value={$formData.Title}
              placeholder="Short summary of the issue"
              class="border-input bg-background w-full rounded-lg border px-3 py-2"
            />
          {/snippet}
        </Control>
        <FieldErrors class="text-destructive mt-1 text-sm" />
      </Field>

      <Field {form} name="Description">
        <Control>
          {#snippet children({ props })}
            <Label class="mb-1 block text-sm font-medium">Description</Label>
            <textarea
              {...props}
              bind:value={$formData.Description}
              rows="5"
              placeholder="Detailed description, steps to reproduce, expected behaviour, etc."
              class="border-input bg-background w-full rounded-lg border px-3 py-2"
            ></textarea>
          {/snippet}
        </Control>
        <FieldErrors class="text-destructive mt-1 text-sm" />
      </Field>

      <div class="grid gap-4 sm:grid-cols-2">
        <Field {form} name="Status">
          <Control>
            {#snippet children({ props })}
              <Label class="mb-1 block text-sm font-medium">Status</Label>
              <select
                {...props}
                bind:value={$formData.Status}
                class="border-input bg-background w-full rounded-lg border px-3 py-2"
              >
                {#each statusOptions as status (status)}
                  <option value={status}>{ticketStatusLabels[status]}</option>
                {/each}
              </select>
            {/snippet}
          </Control>
          <FieldErrors class="text-destructive mt-1 text-sm" />
        </Field>

        <Field {form} name="Priority">
          <Control>
            {#snippet children({ props })}
              <Label class="mb-1 block text-sm font-medium">Priority</Label>
              <select
                {...props}
                bind:value={$formData.Priority}
                class="border-input bg-background w-full rounded-lg border px-3 py-2"
              >
                {#each priorityOptions as priority (priority)}
                  <option value={priority}>{ticketPriorityLabels[priority]}</option>
                {/each}
              </select>
            {/snippet}
          </Control>
          <FieldErrors class="text-destructive mt-1 text-sm" />
        </Field>
      </div>

      <div class="grid gap-4 sm:grid-cols-2">
        <Field {form} name="TeamId">
          <Control>
            {#snippet children({ props })}
              <Label class="mb-1 block text-sm font-medium">Team</Label>
              <input
                {...props}
                bind:value={$formData.TeamId}
                placeholder="Team id (GUID)"
                class="border-input bg-background w-full rounded-lg border px-3 py-2"
              />
            {/snippet}
          </Control>
          <FieldErrors class="text-destructive mt-1 text-sm" />
        </Field>

        <Field {form} name="ProjectId">
          <Control>
            {#snippet children({ props })}
              <Label class="mb-1 block text-sm font-medium">Project</Label>
              <input
                {...props}
                bind:value={$formData.ProjectId}
                placeholder="Project id (GUID)"
                class="border-input bg-background w-full rounded-lg border px-3 py-2"
              />
            {/snippet}
          </Control>
          <FieldErrors class="text-destructive mt-1 text-sm" />
        </Field>
      </div>

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
