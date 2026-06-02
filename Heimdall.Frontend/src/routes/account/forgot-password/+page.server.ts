/**
 * Server-side forgot-password form action — ports `ForgotPassword.razor` into a
 * SvelteKit form action (Phase 6.3, step 9 completion).
 *
 * The endpoint is intentionally generic: `POST /api/v1/auth/forgot-password`
 * ALWAYS returns `200 { status: "ok" }` regardless of whether the email maps to
 * an account, so we cannot (and must not) reveal account existence. Any 200 →
 * redirect to the static confirmation page. The only error branch is `404`,
 * emitted when the email flow is disabled on the API.
 *
 * No `Set-Cookie` forwarding: this endpoint never authenticates the caller.
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

export const actions: Actions = {
  default: async ({ request, fetch }: RequestEvent) => {
    const form = await request.formData();
    const email = form.get('email');
    const emailValue = typeof email === 'string' ? email : '';

    const response = await fetch(`${internalApiOrigin()}/api/v1/auth/forgot-password`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ email: emailValue }),
    });

    // Email reset flow disabled on the API → friendly "not available" copy.
    if (response.status === 404) {
      return fail(404, { error: 'reset_unavailable', email: emailValue });
    }

    // Any 200 is intentionally generic — never confirm whether an account
    // existed. An unexpected non-ok status surfaces a generic error rather than
    // leaking by falling through to the confirmation page.
    if (!response.ok) {
      return fail(400, { error: 'reset_failed', email: emailValue });
    }

    redirect(303, '/account/forgot-password/confirmation');
  },
};
