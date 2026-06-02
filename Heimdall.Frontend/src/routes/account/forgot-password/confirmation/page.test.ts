/**
 * Unit spec for the forgot-password confirmation page
 * (`/account/forgot-password/confirmation` +page.svelte).
 *
 * Static, public "check your email" page whose copy is generic by design (never
 * confirms whether an account existed). Asserts the landmark, heading, the
 * non-leaking body copy, the decorative envelope, and the "Back to sign in"
 * link target.
 */
import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/svelte';

import Page from './+page.svelte';

describe('/account/forgot-password/confirmation +page.svelte', () => {
  it('renders the data-testid="forgot-password-confirmation" landmark', () => {
    render(Page);

    expect(screen.getByTestId('forgot-password-confirmation')).toBeInTheDocument();
  });

  it('renders the "Check your email" heading (level 1)', () => {
    render(Page);

    expect(screen.getByRole('heading', { level: 1, name: 'Check your email' })).toBeInTheDocument();
  });

  it('renders the generic, non-account-leaking body copy', () => {
    render(Page);

    expect(screen.getByText(/if an account exists for the email you entered/i)).toBeInTheDocument();
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
