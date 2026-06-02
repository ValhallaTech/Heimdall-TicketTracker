<script lang="ts">
  /**
   * Root error boundary — ports both `NotFound.razor` (404) and `Error.razor`
   * (generic) into a single SvelteKit `+error.svelte` (Phase 6.3).
   *
   * Uses `$app/state`'s `page` rune (SvelteKit >= 2.12) to read `page.status`
   * and `page.error`. When the status is 404 we render the "Page not found"
   * copy with a CTA back to `/tickets`; otherwise we render the generic "An
   * error occurred" copy and surface the error message (used by `Error.razor`
   * to show the request id) when present.
   *
   * Decorative glyphs are inline SVG marked `aria-hidden` (no Font Awesome).
   */
  import { page } from '$app/state';
  import { resolve } from '$app/paths';

  const isNotFound = $derived(page.status === 404);
  const errorMessage = $derived(page.error?.message ?? '');
</script>

<svelte:head>
  <title>{isNotFound ? 'Page not found' : 'Error'} — Heimdall TicketTracker</title>
</svelte:head>

<section
  class="text-foreground mx-auto flex max-w-xl flex-col items-center px-4 py-16 text-center"
  data-testid="error-boundary"
  data-status={page.status}
>
  {#if isNotFound}
    <span class="text-muted-foreground mb-3" aria-hidden="true">
      <svg
        xmlns="http://www.w3.org/2000/svg"
        viewBox="0 0 512 512"
        class="h-16 w-16"
        fill="currentColor"
      >
        <path
          d="M256 512A256 256 0 1 0 256 0a256 256 0 1 0 0 512zM169.8 165.3c7.9-22.3 29.1-37.3 52.8-37.3l58.3 0c34.9 0 63.1 28.3 63.1 63.1c0 22.6-12.1 43.5-31.7 54.8L296 313.8c-.2 13.3-11.1 24-24.4 24c-13.5 0-24.4-10.9-24.4-24.4l0-13.4c0-8.7 4.7-16.7 12.3-21l44.3-25.4c4.5-2.6 7.3-7.4 7.3-12.6c0-8-6.5-14.6-14.6-14.6l-58.3 0c-3.3 0-6.3 2.1-7.4 5.3l-.3 .9c-4.4 12.5-18.2 19-30.6 14.6s-19-18.2-14.6-30.6l.3-.9zM224 392a32 32 0 1 1 64 0 32 32 0 1 1 -64 0z"
        />
      </svg>
    </span>

    <p class="text-muted-foreground text-7xl font-bold" aria-hidden="true">404</p>
    <h1 class="mt-2 text-2xl font-semibold">Page not found</h1>
    <p class="text-muted-foreground mt-2 mb-6">
      The page you&rsquo;re looking for doesn&rsquo;t exist or has been moved.
    </p>

    <a
      href={resolve('/tickets')}
      class="bg-primary text-primary-foreground hover:bg-primary/90 inline-flex items-center gap-2 rounded-lg px-5 py-2.5 font-medium transition-colors"
    >
      Back to Tickets
    </a>
  {:else}
    <div
      class="border-destructive/40 bg-destructive/10 text-foreground flex w-full items-start gap-3 rounded-lg border p-4 text-left"
      role="alert"
    >
      <span class="text-destructive" aria-hidden="true">
        <svg
          xmlns="http://www.w3.org/2000/svg"
          viewBox="0 0 512 512"
          class="h-8 w-8 shrink-0"
          fill="currentColor"
        >
          <path
            d="M256 32c14.2 0 27.3 7.5 34.5 19.8l216 368c7.3 12.4 7.3 27.7 .2 40.1S486.3 480 472 480L40 480c-14.3 0-27.6-7.7-34.7-20.1s-7-27.8 .2-40.1l216-368C228.7 39.5 241.8 32 256 32zm0 128c-13.3 0-24 10.7-24 24l0 112c0 13.3 10.7 24 24 24s24-10.7 24-24l0-112c0-13.3-10.7-24-24-24zm32 224a32 32 0 1 0 -64 0 32 32 0 1 0 64 0z"
          />
        </svg>
      </span>
      <div>
        <h1 class="text-xl font-semibold">An error occurred</h1>
        <p class="mt-1 mb-0">An error occurred while processing your request.</p>
        {#if errorMessage}
          <p class="mt-2 mb-0 text-sm">
            <strong>Request ID:</strong> <code>{errorMessage}</code>
          </p>
        {/if}
      </div>
    </div>

    <div class="mt-6">
      <a
        href={resolve('/tickets')}
        class="border-input text-foreground hover:bg-accent inline-flex items-center gap-2 rounded-lg border px-5 py-2.5 font-medium transition-colors"
      >
        Back to TicketTracker
      </a>
    </div>
  {/if}
</section>
