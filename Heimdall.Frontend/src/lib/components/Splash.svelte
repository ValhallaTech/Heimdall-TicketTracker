<script lang="ts">
  /**
   * Splash — public landing surface, ported from `Splash.razor` (Phase 6.3).
   *
   * Svelte 5 runes only ($props). Mirrors the Blazor splash: a decorative
   * shield, the product heading, the marketing tagline, and a primary CTA into
   * the ticket queue. Authored by hand with Tailwind utility classes (no
   * Bootstrap, no Font Awesome — the shield is an inline SVG marked
   * `aria-hidden` per the Razor original).
   *
   * Prop contract is preserved from the Phase 6.1 scaffold so the locked specs
   * (`Splash.test.ts`, route `page.smoke.test.ts`) stay green: `title` defaults
   * to "Heimdall" and `tagline` defaults to "Ticket tracking, guarded." and both
   * are rendered. The parity hero copy ("The watchful guardian…") is rendered as
   * an additional descriptive line so the marketing copy is present without
   * changing the default-prop contract those specs assert against.
   */
  import { resolve } from '$app/paths';

  interface SplashProps {
    /** Heading shown on the splash screen. */
    title?: string;
    /** Short tagline beneath the heading. */
    tagline?: string;
  }

  let { title = 'Heimdall', tagline = 'Ticket tracking, guarded.' }: SplashProps = $props();
</script>

<section
  class="bg-background text-foreground flex min-h-screen flex-col items-center justify-center gap-4 p-8 text-center"
  data-testid="splash"
>
  <span class="text-primary mb-2" aria-hidden="true">
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

  <h1 class="text-4xl font-semibold tracking-tight">{title}</h1>

  <p class="text-muted-foreground max-w-prose text-lg">{tagline}</p>

  <p class="text-muted-foreground max-w-prose">
    The watchful guardian of your work. Track tickets across the Nine Realms.
  </p>

  <a
    href={resolve('/tickets')}
    class="bg-primary text-primary-foreground hover:bg-primary/90 mt-2 inline-flex items-center gap-2 rounded-lg px-6 py-3 text-base font-medium shadow transition-colors"
  >
    Open Tickets
  </a>
</section>
