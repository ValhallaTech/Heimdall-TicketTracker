<script lang="ts">
  /**
   * Register confirmation ("check your email") — ports
   * `RegisterConfirmation.razor` (Phase 6.3). Static, public. Generic copy that
   * never leaks account existence, matching the Razor original. Tailwind only;
   * decorative envelope is an inline SVG marked `aria-hidden`.
   */
  import { resolve } from '$app/paths';
</script>

<svelte:head>
  <title>Confirm your email · Heimdall TicketTracker</title>
</svelte:head>

<section class="mx-auto w-full max-w-sm px-4 py-12">
  <div
    class="border-border bg-card text-card-foreground rounded-xl border p-6 text-center shadow-sm sm:p-8"
    data-testid="register-confirmation"
  >
    <span class="text-primary mb-3 inline-block" aria-hidden="true">
      <svg
        xmlns="http://www.w3.org/2000/svg"
        viewBox="0 0 512 512"
        class="h-12 w-12"
        fill="currentColor"
      >
        <path
          d="M48 64C21.5 64 0 85.5 0 112c0 15.1 7.1 29.3 19.2 38.4L236.8 313.6c11.4 8.5 27 8.5 38.4 0L492.8 150.4c12.1-9.1 19.2-23.3 19.2-38.4c0-26.5-21.5-48-48-48L48 64zM0 176L0 384c0 35.3 28.7 64 64 64l384 0c35.3 0 64-28.7 64-64l0-208L294.4 339.2c-22.8 17.1-54 17.1-76.8 0L0 176z"
        />
      </svg>
    </span>
    <h1 class="mb-3 text-2xl font-bold">Check your email</h1>
    <p class="text-muted-foreground mb-6">
      We have sent a confirmation link to your email address. Click the link to activate your
      account, then sign in.
    </p>

    <a
      href={resolve('/login')}
      class="bg-primary text-primary-foreground hover:bg-primary/90 inline-flex w-full items-center justify-center gap-2 rounded-lg px-4 py-2.5 font-medium transition-colors"
    >
      Back to sign in
    </a>
  </div>
</section>
