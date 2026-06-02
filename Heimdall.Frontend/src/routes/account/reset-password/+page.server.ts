/**
 * Server-side reset-password form action — ports `ResetPassword.razor` into a
 * SvelteKit form action (Phase 6.3, step 9 completion).
 *
 * POSTs `{ email, token, password, confirmPassword }` to the internal
 * `POST /api/v1/auth/reset-password` endpoint. The `email`/`token` pair arrives
 * from the emailed reset link (carried through hidden form fields populated by
 * `+page.ts`); the new password is only ever seen server-side.
 *
 * Contract:
 *   200 { status: "ok" }                                   → redirect 303 /login?reset=success
 *   400 { error: "password_mismatch" | "invalid_token" }   → fail 400
 *   404 (reset disabled)                                   → fail 404
 *
 * No `Set-Cookie` forwarding: a successful reset does not authenticate — the
 * user is redirected to sign in.
 */
import { env } from '$env/dynamic/private';
import { fail, redirect } from '@sveltejs/kit';
import type { Actions, RequestEvent } from './$types';

/** Dev default mirrors `hooks.server.ts` — loopback to the co-located API. */
const DEFAULT_INTERNAL_API_ORIGIN = 'http://127.0.0.1:5000';

/** Resolve the API origin at call time so test/runtime env changes are honoured. */
function internalApiOrigin(): string {
  return env.INTERNAL_API_ORIGIN ?? DEFAULT_INTERNAL_API_ORIGIN;
}

/** Narrow an untrusted response body to its `error` string. */
function extractError(body: unknown): string | null {
  if (typeof body !== 'object' || body === null) {
    return null;
  }
  const error = (body as Record<string, unknown>).error;
  return typeof error === 'string' ? error : null;
}

/** Read a JSON body defensively — a malformed/empty body narrows to `null`. */
async function readJsonBody(response: Response): Promise<unknown> {
  try {
    return (await response.json()) as unknown;
  } catch {
    return null;
  }
}

export const actions: Actions = {
  default: async ({ request, fetch }: RequestEvent) => {
    const form = await request.formData();
    const email = form.get('email');
    const token = form.get('token');
    const password = form.get('password');
    const confirmPassword = form.get('confirmPassword');
    const emailValue = typeof email === 'string' ? email : '';
    const tokenValue = typeof token === 'string' ? token : '';

    const response = await fetch(`${internalApiOrigin()}/api/v1/auth/reset-password`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        email: emailValue,
        token: tokenValue,
        password: typeof password === 'string' ? password : '',
        confirmPassword: typeof confirmPassword === 'string' ? confirmPassword : '',
      }),
    });

    if (response.ok) {
      redirect(303, '/login?reset=success');
    }

    // Reset flow disabled on the API → friendly "not available" copy. Carry the
    // email/token through so hidden fields stay populated on re-render.
    if (response.status === 404) {
      return fail(404, {
        error: 'reset_unavailable',
        email: emailValue,
        token: tokenValue,
      });
    }

    const body = await readJsonBody(response);
    const error = extractError(body);

    if (error === 'password_mismatch') {
      return fail(400, { error: 'password_mismatch', email: emailValue, token: tokenValue });
    }

    // `invalid_token` (or any other/unknown failure) → invalid/expired link copy.
    return fail(400, { error: 'invalid_token', email: emailValue, token: tokenValue });
  },
};
