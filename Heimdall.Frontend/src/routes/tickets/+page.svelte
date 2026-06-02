<script lang="ts">
  /**
   * Tickets list page — ports `Tickets.razor` (Phase 6.4).
   *
   * Renders the rows through the shared {@link TicketQueueTable}, plus a search
   * + sort toolbar (a GET `<form>`) and pagination controls (plain links) that
   * drive the URL query string, so the page works under SSR without client JS.
   *
   * Deviation from parity: `Tickets.razor` uses clickable sortable column
   * headers. To keep {@link TicketQueueTable} reusable (the team queue is not
   * sortable), sorting is exposed here as a toolbar control rather than baked
   * into the shared table's headers. The sort fields mirror the Razor columns.
   */
  import { resolve } from '$app/paths';
  import TicketQueueTable from '$lib/components/TicketQueueTable.svelte';
  import type { PageData } from './$types';

  let { data }: { data: PageData } = $props();

  const result = $derived(data.tickets);
  const query = $derived(data.query);

  /** Sort fields offered in the toolbar, mirroring the Razor columns. */
  const sortFields: { value: string; label: string }[] = [
    { value: 'Title', label: 'Title' },
    { value: 'Status', label: 'Status' },
    { value: 'Priority', label: 'Priority' },
    { value: 'ReporterId', label: 'Reporter' },
    { value: 'AssigneeId', label: 'Assignee' },
    { value: 'DateCreated', label: 'Created' },
  ];

  /** First / last item indices shown (1-based) for the results summary. */
  const firstItem = $derived(result.TotalCount === 0 ? 0 : (result.Page - 1) * result.PageSize + 1);
  const lastItem = $derived(Math.min(result.Page * result.PageSize, result.TotalCount));
</script>

<svelte:head>
  <title>Tickets — Heimdall TicketTracker</title>
</svelte:head>

<section class="mx-auto w-full max-w-5xl px-4 py-8" data-testid="tickets-page">
  <div class="mb-6 flex items-center justify-between gap-4">
    <div>
      <h1 class="text-2xl font-semibold">Tickets</h1>
      <p class="text-muted-foreground text-sm">All open and closed work items</p>
    </div>
    <a
      href={resolve('/tickets/new')}
      class="bg-primary text-primary-foreground hover:bg-primary/90 inline-flex items-center gap-2 rounded-lg px-4 py-2 font-medium transition-colors"
    >
      New ticket
    </a>
  </div>

  <form method="GET" action={resolve('/tickets')} class="mb-4 flex flex-wrap items-end gap-3">
    <div class="flex-1">
      <label for="searchText" class="text-muted-foreground mb-1 block text-sm">Search</label>
      <input
        id="searchText"
        name="searchText"
        type="search"
        value={query.searchText}
        placeholder="Search tickets…"
        class="border-input bg-background focus:ring-ring w-full rounded-lg border px-3 py-2 focus:ring-2 focus:outline-none"
      />
    </div>
    <div>
      <label for="sortField" class="text-muted-foreground mb-1 block text-sm">Sort by</label>
      <select
        id="sortField"
        name="sortField"
        class="border-input bg-background rounded-lg border px-3 py-2"
      >
        {#each sortFields as field (field.value)}
          <option value={field.value} selected={field.value === query.sortField}>
            {field.label}
          </option>
        {/each}
      </select>
    </div>
    <div>
      <label for="sortDirection" class="text-muted-foreground mb-1 block text-sm">Direction</label>
      <select
        id="sortDirection"
        name="sortDirection"
        class="border-input bg-background rounded-lg border px-3 py-2"
      >
        <option value="Ascending" selected={query.sortDirection === 'Ascending'}>Ascending</option>
        <option value="Descending" selected={query.sortDirection === 'Descending'}>
          Descending
        </option>
      </select>
    </div>
    <div>
      <label for="pageSize" class="text-muted-foreground mb-1 block text-sm">Per page</label>
      <select
        id="pageSize"
        name="pageSize"
        class="border-input bg-background rounded-lg border px-3 py-2"
      >
        {#each [10, 25, 50, 100] as size (size)}
          <option value={size} selected={size === query.pageSize}>{size}</option>
        {/each}
      </select>
    </div>
    <button
      type="submit"
      class="border-input hover:bg-accent rounded-lg border px-4 py-2 font-medium transition-colors"
    >
      Apply
    </button>
  </form>

  {#if result.TotalCount === 0}
    <div class="border-border rounded-xl border p-10 text-center shadow-sm">
      {#if query.searchText.length > 0}
        <h2 class="text-lg font-semibold">No tickets found</h2>
        <p class="text-muted-foreground mt-1">
          No tickets match “{query.searchText}”. Try a different search.
        </p>
        <a class="text-primary mt-3 inline-block hover:underline" href={resolve('/tickets')}>
          Clear search
        </a>
      {:else}
        <h2 class="text-lg font-semibold">No tickets yet</h2>
        <p class="text-muted-foreground mt-1">Open your first ticket to get started.</p>
        <a
          class="bg-primary text-primary-foreground hover:bg-primary/90 mt-4 inline-flex items-center gap-2 rounded-lg px-4 py-2 font-medium transition-colors"
          href={resolve('/tickets/new')}
        >
          New ticket
        </a>
      {/if}
    </div>
  {:else}
    <TicketQueueTable tickets={result.Items} />

    <div class="mt-4 flex flex-col items-center justify-between gap-2 sm:flex-row">
      <p class="text-muted-foreground text-sm">
        Showing <strong>{firstItem}–{lastItem}</strong> of <strong>{result.TotalCount}</strong>
        {result.TotalCount === 1 ? 'ticket' : 'tickets'}
      </p>

      {#if result.TotalPages > 1}
        <nav aria-label="Pagination" class="flex items-center gap-1">
          {#if result.Page > 1}
            {@render pageButton(result.Page - 1, 'Previous', 'Previous page')}
          {:else}
            <span
              class="border-input text-muted-foreground cursor-not-allowed rounded-lg border px-3 py-1.5 text-sm opacity-50"
              aria-disabled="true"
            >
              Previous
            </span>
          {/if}

          <span class="text-muted-foreground px-2 text-sm" aria-current="page">
            Page {result.Page} of {result.TotalPages}
          </span>

          {#if result.Page < result.TotalPages}
            {@render pageButton(result.Page + 1, 'Next', 'Next page')}
          {:else}
            <span
              class="border-input text-muted-foreground cursor-not-allowed rounded-lg border px-3 py-1.5 text-sm opacity-50"
              aria-disabled="true"
            >
              Next
            </span>
          {/if}
        </nav>
      {/if}
    </div>
  {/if}
</section>

<!--
  Pagination uses GET `<form>`s (not `<a href>`s) so the page query string is
  carried via hidden inputs. This sidesteps `svelte/no-navigation-without-resolve`
  (which only allows literal `resolve()` calls in `href`, with no query support)
  while keeping navigation fully SSR-friendly and base-path aware via `action`.
-->
{#snippet pageButton(targetPage: number, label: string, ariaLabel: string)}
  <form method="GET" action={resolve('/tickets')} class="inline">
    <input type="hidden" name="page" value={targetPage} />
    <input type="hidden" name="pageSize" value={query.pageSize} />
    {#if query.searchText.length > 0}
      <input type="hidden" name="searchText" value={query.searchText} />
    {/if}
    <input type="hidden" name="sortField" value={query.sortField} />
    <input type="hidden" name="sortDirection" value={query.sortDirection} />
    <button
      type="submit"
      class="border-input hover:bg-accent rounded-lg border px-3 py-1.5 text-sm"
      aria-label={ariaLabel}
    >
      {label}
    </button>
  </form>
{/snippet}
