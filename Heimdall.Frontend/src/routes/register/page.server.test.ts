/**
 * STUB — Unit spec for the register form action (`/register` +page.server.ts).
 *
 * The JS/TS Unit Test Engineer owns the real bodies. This stub only enumerates
 * the intended coverage. See `login/page.server.test.ts` for the `fetch`/`env`
 * mocking pattern and redirect/`fail` assertions to mirror.
 */
import { describe, it } from 'vitest';

describe('/register +page.server.ts action', () => {
  // STUB: POSTs { email, password, confirmPassword } to
  // {INTERNAL_API_ORIGIN}/api/v1/auth/register.
  it.todo('posts the registration payload to the internal API');

  // STUB: 200 → redirect(303, '/register/confirmation').
  it.todo('redirects to the confirmation page on success');

  // STUB: 400 { error } → fail(400, { error, email }) for invalid_email /
  // password_mismatch / registration_failed (with codes).
  it.todo('maps 400 error codes to fail payloads and repopulates email');

  // STUB: 404 → fail(404, { error: "registration_unavailable", email }).
  it.todo('maps 404 to registration_unavailable');
});
