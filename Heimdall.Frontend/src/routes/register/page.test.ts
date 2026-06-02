/**
 * STUB — Unit spec for the register page (`/register` +page.svelte).
 *
 * The JS/TS Unit Test Engineer owns the real bodies. This stub only enumerates
 * the intended coverage so the suite is discoverable. See `login/page.test.ts`
 * for the `$app/forms` / `$app/state` mocking pattern and the error-code →
 * copy / email-prefill assertions to mirror.
 */
import { describe, it } from 'vitest';

describe('/register +page.svelte', () => {
  // STUB: render the register form (data-testid="register-form") with labelled
  // email / password / confirm-password inputs and correct autocomplete attrs.
  it.todo('renders the register form with accessible fields');

  // STUB: maps form.error="invalid_email" → "Enter a valid email address."
  it.todo('maps invalid_email to friendly copy');

  // STUB: maps form.error="password_mismatch" → "Passwords do not match."
  it.todo('maps password_mismatch to friendly copy');

  // STUB: maps form.error="registration_failed" → "Could not create the account."
  // and renders any surfaced identity `codes`.
  it.todo('maps registration_failed to generic copy and lists codes');

  // STUB: maps form.error="registration_unavailable" → not-available copy.
  it.todo('maps registration_unavailable to friendly copy');

  // STUB: repopulates the email field from form.email on failure.
  it.todo('repopulates email on failure');
});
