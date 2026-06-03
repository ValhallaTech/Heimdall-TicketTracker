/**
 * Server-side login form action — ports the credential POST from `Login.razor`
 * into a SvelteKit form action (Phase 6.3).
 *
 * Why server-only: the credential exchange and the resulting
 * `__Host-heimdall_refresh` cookie must never be visible to client JS. The
 * action POSTs `{ email, password }` to the internal `POST /api/v1/auth/token`
 * endpoint (same origin-resolution pattern as `hooks.server.ts`), then
 * forwards the API's `Set-Cookie` header(s) onto the SvelteKit response via
 * `cookies.set` before redirecting.
 *
 * Cookie forwarding: the SvelteKit docs (kit/load §Cookies) state you CANNOT
 * emit a `set-cookie` through `setHeaders` — you must use `cookies.set`. So we
 * parse each raw `Set-Cookie` string returned by `response.headers.getSetCookie()`
 * and re-emit it with the same name/value/attributes through `cookies.set`,
 * which serialises it onto the action's (redirect) response.
 *
 * MFA: the API returns `401 { requires_two_factor: true }` when credentials are
 * valid but the user is MFA-enrolled (see `ApiAuthEndpoints.RequiresTwoFactor`).
 * MFA sign-in is Phase 6.5, so we surface a friendly "not yet available"
 * message rather than attempting the challenge.
 */
import { env } from '$env/dynamic/private';
import { fail, redirect } from '@sveltejs/kit';
import type { Actions, RequestEvent } from './$types';
import type { Cookies } from '@sveltejs/kit';

/** Dev default mirrors `hooks.server.ts` — loopback to the co-located API. */
const DEFAULT_INTERNAL_API_ORIGIN = 'http://127.0.0.1:5000';

/** Resolve the API origin at call time so test/runtime env changes are honoured. */
function internalApiOrigin(): string {
  return env.INTERNAL_API_ORIGIN ?? DEFAULT_INTERNAL_API_ORIGIN;
}

/** Default post-login destination when no (safe) returnUrl is supplied. */
const DEFAULT_RETURN_URL = '/tickets';

/**
 * Validate an untrusted `returnUrl`. Only same-site, root-relative paths are
 * allowed: it must start with a single `/` (not `//`, which the browser treats
 * as a protocol-relative absolute URL) to prevent open-redirects.
 */
function safeReturnUrl(returnUrl: FormDataEntryValue | null): string {
  if (typeof returnUrl !== 'string') {
    return DEFAULT_RETURN_URL;
  }
  if (returnUrl.startsWith('/') && !returnUrl.startsWith('//')) {
    return returnUrl;
  }
  return DEFAULT_RETURN_URL;
}

/** Narrow an untrusted response body to its OAuth-style `error` string. */
function extractError(body: unknown): string | null {
  if (typeof body !== 'object' || body === null) {
    return null;
  }
  const error = (body as Record<string, unknown>).error;
  return typeof error === 'string' ? error : null;
}

/** Narrow an untrusted response body to the `requires_two_factor` flag. */
function isTwoFactorRequired(body: unknown): boolean {
  if (typeof body !== 'object' || body === null) {
    return false;
  }
  return (body as Record<string, unknown>).requires_two_factor === true;
}

/** Read a JSON body defensively — a malformed/empty body narrows to `null`. */
async function readJsonBody(response: Response): Promise<unknown> {
  try {
    return (await response.json()) as unknown;
  } catch {
    return null;
  }
}

/**
 * Options accepted by SvelteKit's `cookies.set`, restricted to the attributes
 * an HTTP `Set-Cookie` header can carry.
 */
interface ParsedCookie {
  name: string;
  value: string;
  options: Parameters<Cookies['set']>[2];
}

/**
 * Parse a single raw `Set-Cookie` header value into the shape `cookies.set`
 * expects, preserving the API's attributes verbatim so refresh-token rotation
 * stays unbroken. `path` defaults to `/` (required by `cookies.set`, and the
 * `__Host-` prefix mandates it anyway).
 */
function parseSetCookie(raw: string): ParsedCookie | null {
  const segments = raw.split(';');
  const [nameValue, ...attributes] = segments;
  const eq = nameValue.indexOf('=');
  if (eq < 0) {
    return null;
  }

  const name = nameValue.slice(0, eq).trim();
  const value = nameValue.slice(eq + 1).trim();
  if (name.length === 0) {
    return null;
  }

  const options: Parameters<Cookies['set']>[2] = { path: '/' };

  for (const attribute of attributes) {
    const [rawKey, ...rawVal] = attribute.split('=');
    const key = rawKey.trim().toLowerCase();
    const val = rawVal.join('=').trim();

    switch (key) {
      case 'path':
        options.path = val.length > 0 ? val : '/';
        break;
      case 'domain':
        options.domain = val;
        break;
      case 'max-age': {
        const parsed = Number.parseInt(val, 10);
        if (!Number.isNaN(parsed)) {
          options.maxAge = parsed;
        }
        break;
      }
      case 'expires': {
        const date = new Date(val);
        if (!Number.isNaN(date.getTime())) {
          options.expires = date;
        }
        break;
      }
      case 'samesite': {
        const lowered = val.toLowerCase();
        if (lowered === 'lax' || lowered === 'strict' || lowered === 'none') {
          options.sameSite = lowered;
        }
        break;
      }
      case 'httponly':
        options.httpOnly = true;
        break;
      case 'secure':
        options.secure = true;
        break;
      default:
        break;
    }
  }

  return { name, value, options };
}

/**
 * Forward every `Set-Cookie` the API returned onto the SvelteKit response.
 * `httpOnly`/`secure` are derived solely from the presence of the matching
 * attribute so we mirror the API exactly (and don't silently upgrade a dev
 * cookie to `Secure`).
 */
function forwardSetCookies(cookies: Cookies, setCookies: string[]): void {
  for (const raw of setCookies) {
    const parsed = parseSetCookie(raw);
    if (parsed === null) {
      continue;
    }
    cookies.set(parsed.name, parsed.value, {
      httpOnly: false,
      secure: false,
      ...parsed.options,
    });
  }
}

export const actions: Actions = {
  default: async ({ request, fetch, cookies }: RequestEvent) => {
    const form = await request.formData();
    const email = form.get('email');
    const password = form.get('password');
    const emailValue = typeof email === 'string' ? email : '';

    const response = await fetch(`${internalApiOrigin()}/api/v1/auth/token`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        email: emailValue,
        password: typeof password === 'string' ? password : '',
      }),
    });

    if (response.ok) {
      forwardSetCookies(cookies, response.headers.getSetCookie());
      redirect(303, safeReturnUrl(form.get('returnUrl')));
    }

    const body = await readJsonBody(response);

    // Valid credentials but MFA-enrolled: 401 { requires_two_factor: true }.
    // MFA sign-in is Phase 6.5 — surface a friendly message, do not challenge.
    if (response.status === 401 && isTwoFactorRequired(body)) {
      return fail(400, { error: 'mfa-unavailable', email: emailValue });
    }

    // Credential mismatch: 400 { error: "invalid_grant" }.
    if (extractError(body) === 'invalid_grant') {
      return fail(400, { error: 'invalid-credentials', email: emailValue });
    }

    // Any other failure (rate-limit, transient API error, unexpected shape).
    return fail(400, { error: 'invalid-credentials', email: emailValue });
  },
};
