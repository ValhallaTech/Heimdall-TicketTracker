<script lang="ts">
  /**
   * Forgot-password page — ports `ForgotPassword.razor` into a SvelteKit form
   * (Phase 6.3, step 9 completion).
   *
   * Posts to the co-located default form action (`+page.server.ts`) which calls
   * the intentionally-generic `POST /api/v1/auth/forgot-password`. On success
   * the action redirects to the static confirmation page (no leaking of account
   * existence). The only error branch is "reset not available" (API 404).
   *
   * Tailwind utilities only (no Bootstrap / Font Awesome). The decorative
   * shield is an inline SVG marked `aria-hidden`.
   */
  import { enhance } from '$app/forms';
  import type { ActionData } from './$types';

  let { form }: { form: ActionData } = $props();

  const hasError = $derived(Boolean(form?.error));

  /** Map the action's error code to user-facing copy. */
  const errorMessage = $derived(
    form?.error === 'reset_unavailable'
      ? 'Password reset is not currently available.'
      : 'We could not process your request. Please try again.',
  );
</script>

<svelte:head>
  <title>Reset your password · Heimdall TicketTracker</title>
</svelte:head>

<section class="mx-auto w-full max-w-sm px-4 py-12">
  <div class="border-border bg-card text-card-foreground rounded-xl border p-6 shadow-sm sm:p-8">
    <div class="mb-6 text-center">
      <span class="text-primary mb-3 inline-block" aria-hidden="true">
        <svg
          xmlns="http://www.w3.org/2000/svg"
          viewBox="0 0 512 512"
          class="h-12 w-12"
          fill="currentColor"
        >
          <path
            d="M256 0c4.6 0 9.2 1 13.4 2.9L457.7 82.8c22 9.3 38.4 31 38.3 57.2c-.5 99.2-41.3 280.7-213.6 363.2c-16.7 8-36.1 8-52.8 0C57.3 420.7 16.5 239.2 16 140c-.1-26.2 16.3-47.9 38.3-57.2L242.7 2.9C246.8 1 251.4 0 256 0z"
          />
        </svg>
      </span>
      <h1 class="text-2xl font-bold">Reset your password</h1>
      <p class="text-muted-foreground mt-2 text-sm">
        Enter your email address and we will send you a link to reset your password.
      </p>
    </div>

    {#if hasError}
      <div
        class="border-destructive/40 bg-destructive/10 text-foreground mb-4 rounded-lg border px-4 py-3 text-sm"
        role="alert"
      >
        {errorMessage}
      </div>
    {/if}

    <form method="POST" use:enhance data-testid="forgot-password-form">
      <div class="mb-4">
        <label for="forgot-email" class="mb-1 block text-sm font-medium">Email address</label>
        <input
          id="forgot-email"
          name="email"
          type="email"
          autocomplete="email"
          required
          value={form?.email ?? ''}
          aria-invalid={hasError ? 'true' : undefined}
          class="border-input bg-background focus:ring-ring w-full rounded-lg border px-3 py-2 focus:ring-2 focus:outline-none"
        />
      </div>

      <button
        type="submit"
        class="bg-primary text-primary-foreground hover:bg-primary/90 w-full rounded-lg px-4 py-2.5 font-medium transition-colors"
      >
        Send reset link
      </button>
    </form>
  </div>
</section>
