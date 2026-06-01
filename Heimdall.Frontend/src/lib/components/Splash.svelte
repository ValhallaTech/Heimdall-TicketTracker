<script lang="ts">
  /**
   * Splash — placeholder landing component for the Phase 6.1 scaffold.
   *
   * Svelte 5 runes only ($props). This is intentionally minimal: it exists so
   * the Phase 6.3 splash port has a target and so the smoke spec/test (authored
   * by the JS/TS Unit Test Engineer) has a stable element to assert against.
   */
  interface SplashProps {
    /** Heading shown on the splash screen. */
    title?: string;
    /** Short tagline beneath the heading. */
    tagline?: string;
  }

  let { title = 'Heimdall', tagline = 'Ticket tracking, guarded.' }: SplashProps = $props();
</script>

<section
  class="bg-background text-foreground flex min-h-screen flex-col items-center justify-center gap-3 p-8 text-center"
  data-testid="splash"
>
  <h1 class="text-4xl font-semibold tracking-tight">{title}</h1>
  <p class="text-muted-foreground max-w-prose text-lg">{tagline}</p>
</section>
