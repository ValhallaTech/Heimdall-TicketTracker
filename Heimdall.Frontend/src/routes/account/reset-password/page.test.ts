/**
 * STUB — Unit spec for the reset-password page
 * (`/account/reset-password` +page.svelte) and its `+page.ts` loader.
 *
 * The JS/TS Unit Test Engineer owns the real bodies. See `login/page.test.ts`
 * for the `$app/forms` mocking pattern to mirror.
 */
import { describe, it } from 'vitest';

describe('/account/reset-password +page.ts load', () => {
  // STUB: extracts email/token from url.searchParams, defaulting to ''.
  it.todo('extracts email and token from the query string');
});

describe('/account/reset-password +page.svelte', () => {
  // STUB: render the form (data-testid="reset-password-form") with new-password
  // and confirm inputs (autocomplete="new-password") plus hidden email/token.
  it.todo('renders the reset-password form with hidden email/token fields');

  // STUB: carries data.email/data.token into hidden fields on first render.
  it.todo('seeds hidden fields from the loader data');

  // STUB: maps form.error="password_mismatch" → "Passwords do not match."
  it.todo('maps password_mismatch to friendly copy');

  // STUB: maps form.error="invalid_token" → invalid/expired link copy.
  it.todo('maps invalid_token to friendly copy');

  // STUB: maps form.error="reset_unavailable" → not-available copy.
  it.todo('maps reset_unavailable to friendly copy');

  // STUB: prefers form-echoed email/token over loader data on failure.
  it.todo('repopulates hidden fields from the form payload on failure');
});
