<script lang="ts">
  /**
   * Sign-in page — ports `Login.razor` into a SvelteKit form (Phase 6.3).
   *
   * Posts to the co-located default form action (`+page.server.ts`) which does
   * the credential exchange server-side. Progressively enhanced with
   * `use:enhance`; on a validation failure the action returns a `form` payload
   * whose `error` code we map to friendly copy mirroring the Razor original,
   * and whose `email` we use to repopulate the field.
   *
   * Tailwind utilities only (no Bootstrap / Font Awesome). The decorative
   * shield is an inline SVG marked `aria-hidden`.
   */
  import { enhance } from '$app/forms';
  import { page } from '$app/state';
  import type { ActionData } from './$types';

  let { form }: { form: ActionData } = $props();

  /** `?returnUrl=` is echoed into a hidden field so the action can honour it. */
  const returnUrl = $derived(page.url.searchParams.get('returnUrl') ?? '');

  const hasError = $derived(Boolean(form?.error));

  /** Map the action's error code to user-facing copy (mirrors `Login.razor`). */
  const errorMessage = $derived(
    form?.error === 'mfa-unavailable'
      ? 'Multi-factor sign-in is not yet available in the new frontend. Please use the existing app to sign in.'
      : 'Invalid email or password.',
  );
</script>

<svelte:head>
  <title>Sign in · Heimdall TicketTracker</title>
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
      <h1 class="text-2xl font-bold">Sign in</h1>
    </div>

    {#if hasError}
      <div
        class="border-destructive/40 bg-destructive/10 text-foreground mb-4 rounded-lg border px-4 py-3 text-sm"
        role="alert"
      >
        {errorMessage}
      </div>
    {/if}

    <form method="POST" use:enhance data-testid="login-form">
      <div class="mb-4">
        <label for="login-email" class="mb-1 block text-sm font-medium">Email address</label>
        <input
          id="login-email"
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
        <label for="login-password" class="mb-1 block text-sm font-medium">Password</label>
        <input
          id="login-password"
          name="password"
          type="password"
          autocomplete="current-password"
          required
          aria-invalid={hasError ? 'true' : undefined}
          class="border-input bg-background focus:ring-ring w-full rounded-lg border px-3 py-2 focus:ring-2 focus:outline-none"
        />
      </div>

      {#if returnUrl}
        <input type="hidden" name="returnUrl" value={returnUrl} />
      {/if}

      <div class="mb-4 flex items-center gap-2">
        <input
          id="login-remember-me"
          name="rememberMe"
          type="checkbox"
          value="true"
          class="border-input rounded"
        />
        <label for="login-remember-me" class="text-sm">Remember me</label>
      </div>

      <button
        type="submit"
        class="bg-primary text-primary-foreground hover:bg-primary/90 w-full rounded-lg px-4 py-2.5 font-medium transition-colors"
      >
        Sign in
      </button>
    </form>
  </div>
</section>
