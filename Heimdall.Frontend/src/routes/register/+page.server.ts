/**
 * Server-side register form action — ports `Register.razor` into a SvelteKit
 * form action (Phase 6.3, step 9 completion).
 *
 * Why server-only: the credential exchange must never be visible to client JS.
 * The action POSTs `{ email, password, confirmPassword }` to the internal
 * `POST /api/v1/auth/register` endpoint (same origin-resolution pattern as
 * `hooks.server.ts` / the login action), then redirects to the static
 * "check your email" confirmation page on success.
 *
 * Unlike the login action this endpoint does NOT issue the refresh cookie, so
 * no `Set-Cookie` forwarding is needed — success is a confirmation-pending
 * state, not an authenticated session.
 *
 * Contract:
 *   200 { status: "confirmation_pending" }                       → redirect 303
 *   400 { error: "invalid_email" | "password_mismatch"
 *               | "registration_failed", codes?: string[] }      → fail 400
 *   404 (registration disabled)                                  → fail 404
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

/** Narrow an untrusted response body to its optional `codes` string array. */
function extractCodes(body: unknown): string[] | undefined {
  if (typeof body !== 'object' || body === null) {
    return undefined;
  }
  const codes = (body as Record<string, unknown>).codes;
  if (Array.isArray(codes) && codes.every((c) => typeof c === 'string')) {
    return codes as string[];
  }
  return undefined;
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
    const password = form.get('password');
    const confirmPassword = form.get('confirmPassword');
    const emailValue = typeof email === 'string' ? email : '';

    const response = await fetch(`${internalApiOrigin()}/api/v1/auth/register`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        email: emailValue,
        password: typeof password === 'string' ? password : '',
        confirmPassword: typeof confirmPassword === 'string' ? confirmPassword : '',
      }),
    });

    if (response.ok) {
      redirect(303, '/register/confirmation');
    }

    // Registration disabled on the API → friendly "not available" copy.
    if (response.status === 404) {
      return fail(404, { error: 'registration_unavailable', email: emailValue });
    }

    const body = await readJsonBody(response);
    const error = extractError(body);

    if (error === 'invalid_email') {
      return fail(400, { error: 'invalid_email', email: emailValue });
    }
    if (error === 'password_mismatch') {
      return fail(400, { error: 'password_mismatch', email: emailValue });
    }

    // `registration_failed` (or any other/unknown failure shape) → generic copy,
    // optionally surfacing the API's identity `codes` for the user.
    return fail(400, {
      error: 'registration_failed',
      email: emailValue,
      codes: extractCodes(body),
    });
  },
};
