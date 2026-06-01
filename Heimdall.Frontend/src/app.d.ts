// See https://svelte.dev/docs/kit/types#app.d.ts
// for information about these interfaces
declare global {
  namespace App {
    // interface Error {}

    /**
     * The authenticated user identity attached to the server request.
     *
     * Derived minimally from the access-token JWT payload in
     * `hooks.server.ts` (see Phase 6.2 step 7 /
     * `blazor-to-svelte-transition.md` §4.2). `id` is the token `sub` claim;
     * the remaining fields are best-effort, present only when the issuing API
     * included the corresponding claim. The browser never receives this — it
     * lives only in `event.locals` for the lifetime of the request.
     */
    interface User {
      id: string;
      email?: string;
      name?: string;
    }

    interface Locals {
      /**
       * The authenticated user, or `null` for anonymous/public browsing.
       * Populated by the `handle` hook after a successful refresh-cookie →
       * access-token exchange.
       */
      user: App.User | null;

      /**
       * The in-memory bearer access token for the current server request, used
       * by downstream `+page.server.ts` / `+layout.server.ts` loaders and form
       * actions to call `Heimdall.Web`. Held only in server memory — never sent
       * to the browser as JS-readable state. `null` when unauthenticated.
       */
      accessToken: string | null;

      /**
       * Server-side OpenFGA permission check. Wraps
       * `POST {INTERNAL_API_ORIGIN}/api/v1/authz/check`, which derives the
       * subject from the bearer token server-side. Resolves `false`
       * (deny-closed) on any non-200 response, network error, or when the
       * request is unauthenticated — UI must fail safe.
       */
      checkPermission: (relation: string, object: string) => Promise<boolean>;
    }

    // interface PageData {}
    // interface PageState {}
    // interface Platform {}
  }
}

export {};
