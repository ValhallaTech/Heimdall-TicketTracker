<script lang="ts">
  /**
   * Team queue page — ports `Teams/Queue.razor` (Phase 6.4).
   *
   * Renders the team's tickets through the shared {@link TicketQueueTable} with
   * a heading showing the team id. The empty / filtered-to-nothing state is
   * handled by the table's `emptyMessage`.
   *
   * The Razor original's per-row Route / Claim / Assign actions and the
   * permission fan-out are intentionally omitted — they depend on server-side
   * permission APIs not exposed to the JSON ticket API yet (tracked as a
   * follow-up alongside the server-side team filter NOTE in the loader).
   */
  import TicketQueueTable from '$lib/components/TicketQueueTable.svelte';
  import type { PageData } from './$types';

  let { data }: { data: PageData } = $props();
</script>

<svelte:head>
  <title>Team queue — Heimdall TicketTracker</title>
</svelte:head>

<section class="mx-auto w-full max-w-5xl px-4 py-8" data-testid="team-queue">
  <div class="mb-6">
    <h1 class="text-2xl font-semibold">Team queue</h1>
    <p class="text-muted-foreground text-sm">
      Tickets routed to team <code class="font-mono">{data.teamId}</code>
    </p>
  </div>

  <TicketQueueTable tickets={data.tickets} emptyMessage="No tickets are routed to this team." />
</section>
