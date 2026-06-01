/**
 * Smoke spec for the splash route (`/`).
 *
 * Phase 6.1 step 3 (phase-6-checklist.md): assert the index route renders the
 * Splash component so the SvelteKit scaffold is verifiably wired up. We render
 * `+page.svelte` (which embeds Splash) and assert on the user-visible surface
 * — the `data-testid="splash"` landmark, the "Heimdall" heading, and the
 * default tagline — rather than implementation details.
 */
import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/svelte';

import Page from './+page.svelte';

describe('/ (splash route) smoke', () => {
  it('renders the Splash component landmark', () => {
    render(Page);

    expect(screen.getByTestId('splash')).toBeInTheDocument();
  });

  it('renders the "Heimdall" heading', () => {
    render(Page);

    expect(screen.getByRole('heading', { level: 1, name: 'Heimdall' })).toBeInTheDocument();
  });

  it('renders the default tagline', () => {
    render(Page);

    expect(screen.getByText('Ticket tracking, guarded.')).toBeInTheDocument();
  });
});
