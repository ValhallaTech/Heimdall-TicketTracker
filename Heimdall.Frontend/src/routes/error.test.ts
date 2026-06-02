// STUB: spec authored by the JS/TS Unit Test Engineer
/**
 * Stub spec for the root error boundary (`+error.svelte`).
 *
 * The component reads `page.status` / `page.error` from `$app/state`, so the
 * test engineer will mock that module to exercise both branches.
 *
 * Cases to cover (real assertions owned by the JS/TS Unit Test Engineer):
 */
import { describe, it } from 'vitest';

describe('+error.svelte (root error boundary)', () => {
  it.todo('renders the data-testid="error-boundary" landmark');
  it.todo('reflects page.status on the data-status attribute');
  it.todo('renders the NotFound copy ("Page not found") when status is 404');
  it.todo('renders a "/tickets" CTA in the 404 branch');
  it.todo('renders the generic "An error occurred" copy for non-404 statuses');
  it.todo('shows the request id (page.error.message) when present');
  it.todo('omits the request id block when page.error has no message');
  it.todo('marks decorative SVG glyphs aria-hidden');
});
