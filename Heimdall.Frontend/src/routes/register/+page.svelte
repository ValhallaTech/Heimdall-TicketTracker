<script lang="ts">
  /**
   * Register page — ports `Register.razor` into a SvelteKit form (Phase 6.3,
   * step 9 completion).
   *
   * Posts to the co-located default form action (`+page.server.ts`) which does
   * the credential exchange server-side against `POST /api/v1/auth/register`.
   * Progressively enhanced with `use:enhance`; on a validation failure the
   * action returns a `form` payload whose `error` code we map to friendly copy,
   * and whose `email` we use to repopulate the field. The generic
   * `registration_failed` branch optionally surfaces the API's identity `codes`.
   *
   * Tailwind utilities only (no Bootstrap / Font Awesome). The decorative
   * shield is an inline SVG marked `aria-hidden`.
   */
  import { enhance } from '$app/forms';
  import type { ActionData } from './$types';

  let { form }: { form: ActionData } = $props();

  const hasError = $derived(Boolean(form?.error));

  /** Map the action's error code to user-facing copy (mirrors `Register.razor`). */
  const errorMessage = $derived(
    form?.error === 'invalid_email'
      ? 'Enter a valid email address.'
      : form?.error === 'password_mismatch'
        ? 'Passwords do not match.'
        : form?.error === 'registration_unavailable'
          ? 'Registration is not currently available.'
          : 'Could not create the account.',
  );

  /** Identity validation codes from the API, surfaced under the generic error. */
  const codes = $derived(form?.error === 'registration_failed' ? (form?.codes ?? []) : []);
</script>

<svelte:head>
  <title>Create account · Heimdall TicketTracker</title>
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
      <h1 class="text-2xl font-bold">Create account</h1>
    </div>

    {#if hasError}
      <div
        class="border-destructive/40 bg-destructive/10 text-foreground mb-4 rounded-lg border px-4 py-3 text-sm"
        role="alert"
      >
        {errorMessage}
        {#if codes.length > 0}
          <ul class="mt-2 list-inside list-disc">
            {#each codes as code (code)}
              <li>{code}</li>
            {/each}
          </ul>
        {/if}
      </div>
    {/if}

    <form method="POST" use:enhance data-testid="register-form">
      <div class="mb-4">
        <label for="register-email" class="mb-1 block text-sm font-medium">Email address</label>
        <input
          id="register-email"
          name="email"
          type="email"
          autocomplete="email"
          required
          value={form?.email ?? ''}
          aria-invalid={hasError ? 'true' : undefined}
          class="border-input bg-background focus:ring-ring w-full rounded-lg border px-3 py-2 focus:ring-2 focus:outline-none"
        />
      </div>

      <div class="mb-4">
        <label for="register-password" class="mb-1 block text-sm font-medium">Password</label>
        <input
          id="register-password"
          name="password"
          type="password"
          autocomplete="new-password"
          required
          aria-invalid={hasError ? 'true' : undefined}
          class="border-input bg-background focus:ring-ring w-full rounded-lg border px-3 py-2 focus:ring-2 focus:outline-none"
        />
      </div>

      <div class="mb-4">
        <label for="register-confirm-password" class="mb-1 block text-sm font-medium">
          Confirm password
        </label>
        <input
          id="register-confirm-password"
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
        Create account
      </button>
    </form>
  </div>
</section>
