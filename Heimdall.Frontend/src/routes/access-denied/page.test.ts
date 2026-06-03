/**
 * Unit spec for the Access Denied (403) page (`/access-denied` +page.svelte).
 *
 * Static, public page — rendered directly. Asserts the landmark, the decorative
 * (aria-hidden) 403 indicator and shield, the heading, the body copy, and the
 * "Back to home" link target.
 */
import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/svelte';

import Page from './+page.svelte';

describe('/access-denied +page.svelte', () => {
  it('renders the data-testid="access-denied" landmark', () => {
    render(Page);

    expect(screen.getByTestId('access-denied')).toBeInTheDocument();
  });

  it('renders the "403" status indicator marked aria-hidden', () => {
    render(Page);

    const indicator = screen.getByText('403');
    expect(indicator).toBeInTheDocument();
    expect(indicator).toHaveAttribute('aria-hidden', 'true');
  });

  it('renders the "Access denied" heading (level 1)', () => {
    render(Page);

    expect(screen.getByRole('heading', { level: 1, name: 'Access denied' })).toBeInTheDocument();
  });

  it('renders the permission-denied body copy', () => {
    render(Page);

    expect(screen.getByText(/don’t have permission to view this page/i)).toBeInTheDocument();
  });

  it('renders a "Back to home" link pointing at root-relative "/"', () => {
    render(Page);

    expect(screen.getByRole('link', { name: 'Back to home' })).toHaveAttribute('href', '/');
  });

  it('marks the decorative shield SVG aria-hidden', () => {
    const { container } = render(Page);

    const hidden = container.querySelectorAll('[aria-hidden="true"]');
    expect(hidden.length).toBeGreaterThanOrEqual(2); // shield span + 403 indicator
  });
});
