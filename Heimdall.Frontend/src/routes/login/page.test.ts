/**
 * Unit spec for the login page (`/login` +page.svelte).
 *
 * The page reads `page.url.searchParams` from `$app/state` (for the returnUrl
 * echo) and progressively enhances the form via `use:enhance` from `$app/forms`.
 * Both are mocked: `page.url` is a mutable URL we set per test, and `enhance` is
 * a spy action so we can assert it was wired onto the form. The `form` action
 * payload is supplied as a prop to drive the error-message and email-prefill
 * branches.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/svelte';

const enhance = vi.fn(() => ({ destroy() {} }));

const pageState: { url: URL } = { url: new URL('http://localhost/login') };

vi.mock('$app/forms', () => ({
  enhance: (...args: unknown[]) => enhance(...(args as [])),
}));

vi.mock('$app/state', () => ({
  get page() {
    return pageState;
  },
}));

import Page from './+page.svelte';

beforeEach(() => {
  pageState.url = new URL('http://localhost/login');
});

afterEach(() => {
  vi.clearAllMocks();
});

describe('/login +page.svelte', () => {
  it('renders the data-testid="login-form" form', () => {
    render(Page, { props: { form: null } });

    expect(screen.getByTestId('login-form')).toBeInTheDocument();
  });

  it('renders email and password inputs with associated <label>s', () => {
    render(Page, { props: { form: null } });

    expect(screen.getByLabelText('Email address')).toBeInTheDocument();
    expect(screen.getByLabelText('Password')).toBeInTheDocument();
  });

  it('renders a remember-me checkbox', () => {
    render(Page, { props: { form: null } });

    expect(screen.getByLabelText('Remember me')).toBeInTheDocument();
  });

  it('omits the hidden returnUrl input when no returnUrl query param is present', () => {
    const { container } = render(Page, { props: { form: null } });

    expect(container.querySelector('input[name="returnUrl"]')).toBeNull();
  });

  it('renders a hidden returnUrl input mirroring the ?returnUrl= query param', () => {
    pageState.url = new URL('http://localhost/login?returnUrl=/tickets/5');
    const { container } = render(Page, { props: { form: null } });

    const hidden = container.querySelector('input[name="returnUrl"]');
    expect(hidden).not.toBeNull();
    expect(hidden).toHaveValue('/tickets/5');
  });

  it('shows the generic "Invalid email or password." copy on error="invalid-credentials"', () => {
    render(Page, { props: { form: { error: 'invalid-credentials' } } });

    expect(screen.getByRole('alert')).toHaveTextContent('Invalid email or password.');
  });

  it('shows the MFA-unavailable copy on error="mfa-unavailable"', () => {
    render(Page, { props: { form: { error: 'mfa-unavailable' } } });

    expect(screen.getByRole('alert')).toHaveTextContent(
      /multi-factor sign-in is not yet available/i,
    );
  });

  it('renders no error alert when form is null', () => {
    render(Page, { props: { form: null } });

    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('prefills the email field from form.email after a failed submit', () => {
    render(Page, { props: { form: { error: 'invalid-credentials', email: 'odin@asgard.test' } } });

    expect(screen.getByLabelText('Email address')).toHaveValue('odin@asgard.test');
  });

  it('sets aria-invalid on the inputs when an error is present', () => {
    render(Page, { props: { form: { error: 'invalid-credentials' } } });

    expect(screen.getByLabelText('Email address')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByLabelText('Password')).toHaveAttribute('aria-invalid', 'true');
  });

  it('progressively enhances submission via use:enhance', () => {
    render(Page, { props: { form: null } });

    expect(enhance).toHaveBeenCalledTimes(1);
    const [node] = enhance.mock.calls[0] as unknown as [HTMLElement];
    expect(node).toBe(screen.getByTestId('login-form'));
  });
});
