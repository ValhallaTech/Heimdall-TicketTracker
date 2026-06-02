/**
 * STUB — Unit spec for the forgot-password form action
 * (`/account/forgot-password` +page.server.ts).
 *
 * The JS/TS Unit Test Engineer owns the real bodies. See
 * `login/page.server.test.ts` for the `fetch`/`env` mocking pattern to mirror.
 */
import { describe, it } from 'vitest';

describe('/account/forgot-password +page.server.ts action', () => {
  // STUB: POSTs { email } to {INTERNAL_API_ORIGIN}/api/v1/auth/forgot-password.
  it.todo('posts the email to the internal API');

  // STUB: 200 → redirect(303, '/account/forgot-password/confirmation')
  // regardless of account existence (generic by design).
  it.todo('always redirects to the confirmation page on success');

  // STUB: 404 → fail(404, { error: "reset_unavailable", email }).
  it.todo('maps 404 to reset_unavailable');
});
