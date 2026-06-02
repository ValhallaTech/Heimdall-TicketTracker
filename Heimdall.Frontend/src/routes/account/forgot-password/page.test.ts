/**
 * STUB — Unit spec for the forgot-password page
 * (`/account/forgot-password` +page.svelte).
 *
 * The JS/TS Unit Test Engineer owns the real bodies. See `login/page.test.ts`
 * for the `$app/forms` mocking pattern to mirror.
 */
import { describe, it } from 'vitest';

describe('/account/forgot-password +page.svelte', () => {
  // STUB: render the form (data-testid="forgot-password-form") with a labelled
  // email input (autocomplete="email").
  it.todo('renders the forgot-password form with an accessible email field');

  // STUB: maps form.error="reset_unavailable" → "Password reset is not currently available."
  it.todo('maps reset_unavailable to friendly copy');

  // STUB: repopulates the email field from form.email on failure.
  it.todo('repopulates email on failure');
});
