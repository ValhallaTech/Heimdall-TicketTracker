// STUB: spec authored by the JS/TS Unit Test Engineer
/**
 * Stub spec for the login page (`/login` +page.svelte).
 *
 * Cases to cover (real assertions owned by the JS/TS Unit Test Engineer):
 */
import { describe, it } from 'vitest';

describe('/login +page.svelte', () => {
  it.todo('renders the data-testid="login-form" form');
  it.todo('renders email and password inputs with associated <label>s');
  it.todo('renders a remember-me checkbox');
  it.todo('omits the hidden returnUrl input when no returnUrl query param is present');
  it.todo('renders a hidden returnUrl input mirroring the ?returnUrl= query param');
  it.todo('shows the generic "Invalid email or password." copy on error="invalid-credentials"');
  it.todo('shows the MFA-unavailable copy on error="mfa-unavailable"');
  it.todo('prefills the email field from form.email after a failed submit');
  it.todo('sets aria-invalid on the inputs when an error is present');
  it.todo('progressively enhances submission via use:enhance');
});
