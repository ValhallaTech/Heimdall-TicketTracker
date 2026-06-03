/**
 * Unit spec for the root error boundary (`+error.svelte`).
 *
 * The component reads `page.status` / `page.error` from `$app/state`, so we mock
 * that module with a mutable `page` object and set it per test before rendering
 * (the component reads it synchronously at init via `$derived`). Both branches
 * are exercised: 404 → "Page not found" + `/tickets` CTA; any other status →
 * the generic "An error occurred" copy, with/without a request-id message.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/svelte';

interface MockPage {
  status: number;
  error: { message: string } | null;
}

const page: MockPage = { status: 500, error: null };

vi.mock('$app/state', () => ({
  get page() {
    return page;
  },
}));

import Page from './+error.svelte';

beforeEach(() => {
  page.status = 500;
  page.error = null;
});

afterEach(() => {
  vi.clearAllMocks();
});

describe('+error.svelte (root error boundary)', () => {
  it('renders the data-testid="error-boundary" landmark', () => {
    render(Page);

    expect(screen.getByTestId('error-boundary')).toBeInTheDocument();
  });

  it('reflects page.status on the data-status attribute', () => {
    page.status = 404;
    render(Page);

    expect(screen.getByTestId('error-boundary')).toHaveAttribute('data-status', '404');
  });

  it('renders the NotFound copy ("Page not found") when status is 404', () => {
    page.status = 404;
    render(Page);

    expect(screen.getByRole('heading', { level: 1, name: 'Page not found' })).toBeInTheDocument();
    expect(screen.getByText('404')).toBeInTheDocument();
  });

  it('renders a "/tickets" CTA in the 404 branch', () => {
    page.status = 404;
    render(Page);

    expect(screen.getByRole('link', { name: 'Back to Tickets' })).toHaveAttribute(
      'href',
      '/tickets',
    );
  });

  it('renders the generic "An error occurred" copy for non-404 statuses', () => {
    page.status = 500;
    render(Page);

    expect(
      screen.getByRole('heading', { level: 1, name: 'An error occurred' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('alert')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Back to TicketTracker' })).toHaveAttribute(
      'href',
      '/tickets',
    );
  });

  it('shows the request id (page.error.message) when present', () => {
    page.status = 500;
    page.error = { message: 'request-id-42' };
    render(Page);

    expect(screen.getByText('Request ID:')).toBeInTheDocument();
    expect(screen.getByText('request-id-42')).toBeInTheDocument();
  });

  it('omits the request id block when page.error has no message', () => {
    page.status = 500;
    page.error = null;
    render(Page);

    expect(screen.queryByText('Request ID:')).not.toBeInTheDocument();
  });

  it('marks decorative SVG glyphs aria-hidden', () => {
    page.status = 404;
    const { container } = render(Page);

    expect(container.querySelector('[aria-hidden="true"]')).not.toBeNull();
  });
});
