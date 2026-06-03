<script lang="ts">
  /**
   * Reset-password page — ports `ResetPassword.razor` into a SvelteKit form
   * (Phase 6.3, step 9 completion).
   *
   * `email` and `token` come from the emailed reset link via `+page.ts` (`data`)
   * and are carried back to the action in hidden fields. On a failure the action
   * echoes them in its `form` payload so they survive re-render. The new
   * password is only ever submitted server-side (`+page.server.ts`).
   *
   * Tailwind utilities only (no Bootstrap / Font Awesome). The decorative
   * shield is an inline SVG marked `aria-hidden`.
   */
  import { enhance } from '$app/forms';
  import type { ActionData, PageData } from './$types';

  let { data, form }: { data: PageData; form: ActionData } = $props();

  const hasError = $derived(Boolean(form?.error));

  /** Prefer the action's echoed values on failure, else the loader's. */
  const email = $derived(form?.email ?? data.email);
  const token = $derived(form?.token ?? data.token);

  /** Map the action's error code to user-facing copy (mirrors `ResetPassword.razor`). */
  const errorMessage = $derived(
    form?.error === 'password_mismatch'
      ? 'Passwords do not match.'
      : form?.error === 'reset_unavailable'
        ? 'Password reset is not currently available.'
        : 'This reset link is invalid or has expired. Request a new one.',
  );
</script>

<svelte:head>
  <title>Choose a new password · Heimdall TicketTracker</title>
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
      <h1 class="text-2xl font-bold">Choose a new password</h1>
    </div>

    {#if hasError}
      <div
        class="border-destructive/40 bg-destructive/10 text-foreground mb-4 rounded-lg border px-4 py-3 text-sm"
        role="alert"
      >
        {errorMessage}
      </div>
    {/if}

    <form method="POST" use:enhance data-testid="reset-password-form">
      <input type="hidden" name="email" value={email} />
      <input type="hidden" name="token" value={token} />

      <div class="mb-4">
        <label for="reset-password" class="mb-1 block text-sm font-medium">New password</label>
        <input
          id="reset-password"
          name="password"
          type="password"
          autocomplete="new-password"
          required
          aria-invalid={hasError ? 'true' : undefined}
          class="border-input bg-background focus:ring-ring w-full rounded-lg border px-3 py-2 focus:ring-2 focus:outline-none"
        />
      </div>

      <div class="mb-4">
        <label for="reset-confirm-password" class="mb-1 block text-sm font-medium">
          Confirm new password
        </label>
        <input
          id="reset-confirm-password"
          name="confirmPassword"
          type="password"
          autocomplete="new-password"
          required
          aria-invalid={hasError ? 'true' : undefined}
          class="border-input bg-background focus:ring-ring w-full rounded-lg border px-3 py-2 focus:ring-2 focus:outline-none"
        />
      </div>

      <button
        type="submit"
        class="bg-primary text-primary-foreground hover:bg-primary/90 w-full rounded-lg px-4 py-2.5 font-medium transition-colors"
      >
        Reset password
      </button>
    </form>
  </div>
</section>
