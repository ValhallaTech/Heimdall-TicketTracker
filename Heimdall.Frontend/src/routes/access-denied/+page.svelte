<script lang="ts">
  /**
   * Access denied (403) — ported from `AccessDenied.razor` (Phase 6.3).
   *
   * Public, static page rendered when an authorization decision denies access.
   * Preserves the Razor copy, the large `403` status indicator (decorative,
   * `aria-hidden`), the decorative shield, and the "Back to home" link. Built
   * with Tailwind utilities; the shield is an inline SVG (no Font Awesome).
   */
  import { resolve } from '$app/paths';
</script>

<svelte:head>
  <title>Access denied — Heimdall TicketTracker</title>
</svelte:head>

<section
  class="text-foreground mx-auto flex max-w-xl flex-col items-center px-4 py-16 text-center"
  data-testid="access-denied"
>
  <span class="text-muted-foreground mb-3" aria-hidden="true">
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 512 512"
      class="h-16 w-16"
      fill="currentColor"
    >
      <path
        d="M256 0c4.6 0 9.2 1 13.4 2.9L457.7 82.8c22 9.3 38.4 31 38.3 57.2c-.5 99.2-41.3 280.7-213.6 363.2c-16.7 8-36.1 8-52.8 0C57.3 420.7 16.5 239.2 16 140c-.1-26.2 16.3-47.9 38.3-57.2L242.7 2.9C246.8 1 251.4 0 256 0z"
      />
    </svg>
  </span>

  <p class="text-muted-foreground text-7xl font-bold" aria-hidden="true">403</p>

  <h1 class="mt-2 text-2xl font-semibold">Access denied</h1>

  <p class="text-muted-foreground mt-2 mb-6">You don&rsquo;t have permission to view this page.</p>

  <a
    href={resolve('/')}
    class="bg-primary text-primary-foreground hover:bg-primary/90 inline-flex items-center gap-2 rounded-lg px-5 py-2.5 font-medium transition-colors"
  >
    Back to home
  </a>
</section>
