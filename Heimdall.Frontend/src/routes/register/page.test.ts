/**
 * Unit spec for the register page (`/register` +page.svelte).
 *
 * The form is progressively enhanced via `use:enhance` from `$app/forms`, which
 * is mocked as a spy action so we can assert it was wired onto the form. The
 * `form` action payload is supplied as a prop to drive the error-message,
 * identity-`codes` listing, and email-prefill branches. Mirrors
 * `login/page.test.ts`.
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

describe('/register +page.svelte', () => {
  it('renders the data-testid="register-form" form', () => {
    render(Page, { props: { form: null } });

    expect(screen.getByTestId('register-form')).toBeInTheDocument();
  });

  it('renders email, password and confirm-password inputs with associated <label>s', () => {
    render(Page, { props: { form: null } });

    expect(screen.getByLabelText('Email address')).toBeInTheDocument();
    expect(screen.getByLabelText('Password')).toBeInTheDocument();
    expect(screen.getByLabelText('Confirm password')).toBeInTheDocument();
  });

  it('sets the expected autocomplete attributes on the credential fields', () => {
    render(Page, { props: { form: null } });

    expect(screen.getByLabelText('Email address')).toHaveAttribute('autocomplete', 'email');
    expect(screen.getByLabelText('Password')).toHaveAttribute('autocomplete', 'new-password');
    expect(screen.getByLabelText('Confirm password')).toHaveAttribute(
      'autocomplete',
      'new-password',
    );
  });

  it('renders no error alert when form is null', () => {
    render(Page, { props: { form: null } });

    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('maps error="invalid_email" to "Enter a valid email address."', () => {
    render(Page, { props: { form: { error: 'invalid_email' } } });

    expect(screen.getByRole('alert')).toHaveTextContent('Enter a valid email address.');
  });

  it('maps error="password_mismatch" to "Passwords do not match."', () => {
    render(Page, { props: { form: { error: 'password_mismatch' } } });

    expect(screen.getByRole('alert')).toHaveTextContent('Passwords do not match.');
  });

  it('maps error="registration_unavailable" to the not-available copy', () => {
    render(Page, { props: { form: { error: 'registration_unavailable' } } });

    expect(screen.getByRole('alert')).toHaveTextContent('Registration is not currently available.');
  });

  it('maps error="registration_failed" to the generic copy', () => {
    render(Page, { props: { form: { error: 'registration_failed' } } });

    expect(screen.getByRole('alert')).toHaveTextContent('Could not create the account.');
  });

  it('lists the surfaced identity codes under the generic registration_failed copy', () => {
    render(Page, {
      props: {
        form: {
          error: 'registration_failed',
          codes: ['PasswordTooShort', 'PasswordRequiresDigit'],
        },
      },
    });

    const alert = screen.getByRole('alert');
    expect(alert).toHaveTextContent('PasswordTooShort');
    expect(alert).toHaveTextContent('PasswordRequiresDigit');
    expect(alert.querySelectorAll('li')).toHaveLength(2);
  });

  it('renders no codes list when registration_failed carries no codes', () => {
    render(Page, { props: { form: { error: 'registration_failed' } } });

    expect(screen.getByRole('alert').querySelector('ul')).toBeNull();
  });

  it('prefills the email field from form.email after a failed submit', () => {
    render(Page, { props: { form: { error: 'invalid_email', email: 'odin@asgard.test' } } });

    expect(screen.getByLabelText('Email address')).toHaveValue('odin@asgard.test');
  });

  it('sets aria-invalid on the inputs when an error is present', () => {
    render(Page, { props: { form: { error: 'invalid_email' } } });

    expect(screen.getByLabelText('Email address')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByLabelText('Password')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByLabelText('Confirm password')).toHaveAttribute('aria-invalid', 'true');
  });

  it('omits aria-invalid when there is no error', () => {
    render(Page, { props: { form: null } });

    expect(screen.getByLabelText('Email address')).not.toHaveAttribute('aria-invalid');
  });

  it('progressively enhances submission via use:enhance', () => {
    render(Page, { props: { form: null } });

    expect(enhance).toHaveBeenCalledTimes(1);
    const [node] = enhance.mock.calls[0] as unknown as [HTMLElement];
    expect(node).toBe(screen.getByTestId('register-form'));
  });
});
