/**
 * Unit spec for the register confirmation page
 * (`/register/confirmation` +page.svelte).
 *
 * Static, public "check your email" page. Asserts the landmark, heading, the
 * generic (non-leaking) body copy, the decorative envelope, and the
 * "Back to sign in" link target.
 */
import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/svelte';

import Page from './+page.svelte';

describe('/register/confirmation +page.svelte', () => {
  it('renders the data-testid="register-confirmation" landmark', () => {
    render(Page);

    expect(screen.getByTestId('register-confirmation')).toBeInTheDocument();
  });

  it('renders the "Check your email" heading (level 1)', () => {
    render(Page);

    expect(screen.getByRole('heading', { level: 1, name: 'Check your email' })).toBeInTheDocument();
  });

  it('renders the confirmation body copy', () => {
    render(Page);

    expect(screen.getByText(/sent a confirmation link to your email address/i)).toBeInTheDocument();
  });

  it('renders a "Back to sign in" link pointing at root-relative "/login"', () => {
    render(Page);

    expect(screen.getByRole('link', { name: 'Back to sign in' })).toHaveAttribute('href', '/login');
  });

  it('marks the decorative envelope SVG aria-hidden', () => {
    const { container } = render(Page);

    expect(container.querySelector('[aria-hidden="true"]')).not.toBeNull();
  });
});
