/**
 * Unit spec for the forgot-password page
 * (`/account/forgot-password` +page.svelte).
 *
 * The form is progressively enhanced via `use:enhance` from `$app/forms`, mocked
 * as a spy action. The `form` action payload is supplied as a prop to drive the
 * error-message and email-prefill branches. Mirrors `login/page.test.ts`.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/svelte';

const enhance = vi.fn(() => ({ destroy() {} }));

vi.mock('$app/forms', () => ({
  enhance: (...args: unknown[]) => enhance(...(args as [])),
}));

import Page from './+page.svelte';

afterEach(() => {
  vi.clearAllMocks();
});

describe('/account/forgot-password +page.svelte', () => {
  it('renders the data-testid="forgot-password-form" form', () => {
    render(Page, { props: { form: null } });

    expect(screen.getByTestId('forgot-password-form')).toBeInTheDocument();
  });

  it('renders an email input with an associated <label> and autocomplete="email"', () => {
    render(Page, { props: { form: null } });

    const email = screen.getByLabelText('Email address');
    expect(email).toBeInTheDocument();
    expect(email).toHaveAttribute('autocomplete', 'email');
  });

  it('renders no error alert when form is null', () => {
    render(Page, { props: { form: null } });

    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('maps error="reset_unavailable" to "Password reset is not currently available."', () => {
    render(Page, { props: { form: { error: 'reset_unavailable' } } });

    expect(screen.getByRole('alert')).toHaveTextContent(
      'Password reset is not currently available.',
    );
  });

  it('maps any other error code to the generic try-again copy', () => {
    render(Page, { props: { form: { error: 'reset_failed' } } });

    expect(screen.getByRole('alert')).toHaveTextContent(
      'We could not process your request. Please try again.',
    );
  });

  it('prefills the email field from form.email after a failed submit', () => {
    render(Page, { props: { form: { error: 'reset_failed', email: 'odin@asgard.test' } } });

    expect(screen.getByLabelText('Email address')).toHaveValue('odin@asgard.test');
  });

  it('sets aria-invalid on the email input when an error is present', () => {
    render(Page, { props: { form: { error: 'reset_unavailable' } } });

    expect(screen.getByLabelText('Email address')).toHaveAttribute('aria-invalid', 'true');
  });

  it('omits aria-invalid when there is no error', () => {
    render(Page, { props: { form: null } });

    expect(screen.getByLabelText('Email address')).not.toHaveAttribute('aria-invalid');
  });

  it('progressively enhances submission via use:enhance', () => {
    render(Page, { props: { form: null } });

    expect(enhance).toHaveBeenCalledTimes(1);
    const [node] = enhance.mock.calls[0] as unknown as [HTMLElement];
    expect(node).toBe(screen.getByTestId('forgot-password-form'));
  });
});
