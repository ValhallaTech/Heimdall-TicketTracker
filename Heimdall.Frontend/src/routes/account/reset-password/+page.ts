/**
 * Reset-password loader — extracts the `email` and `token` reset parameters from
 * the URL query (`?email=&token=`) so the page can carry them in hidden form
 * fields back to the action. Ports the query-binding behaviour of
 * `ResetPassword.razor` (Phase 6.3, step 9 completion).
 *
 * These values are not secrets in the session sense — they arrive in the
 * emailed reset link — but the new password is only ever submitted server-side
 * via the form action, never via client `fetch`.
 */
import type { PageLoad } from './$types';

export const load: PageLoad = ({ url }) => {
  return {
    email: url.searchParams.get('email') ?? '',
    token: url.searchParams.get('token') ?? '',
  };
};
