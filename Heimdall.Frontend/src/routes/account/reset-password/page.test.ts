/**
 * Unit spec for the reset-password page (`/account/reset-password` +page.svelte)
 * and its `+page.ts` loader.
 *
 * The loader pulls `email`/`token` off `url.searchParams` (defaulting to `''`).
 * The page is progressively enhanced via `use:enhance` from `$app/forms`, mocked
 * as a spy action. The hidden `email`/`token` fields are seeded from the loader
 * `data` and prefer the action's echoed `form` values on failure. Mirrors
 * `login/page.test.ts`.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/svelte';

import { load } from './+page';

const enhance = vi.fn(() => ({ destroy() {} }));

vi.mock('$app/forms', () => ({
  enhance: (...args: unknown[]) => enhance(...(args as [])),
}));

import Page from './+page.svelte';

/** Minimal `LoadEvent` shape — the loader only reads `url.searchParams`. */
function loadEvent(url: string): Parameters<typeof load>[0] {
  return { url: new URL(url) } as unknown as Parameters<typeof load>[0];
}

function hiddenValue(container: HTMLElement, name: string): string | null {
  return container.querySelector<HTMLInputElement>(`input[name="${name}"]`)?.value ?? null;
}

afterEach(() => {
  vi.clearAllMocks();
});

describe('/account/reset-password +page.ts load', () => {
  it('extracts email and token from the query string', () => {
    const result = load(
      loadEvent('http://localhost/account/reset-password?email=odin@asgard.test&token=abc123'),
    );

    expect(result).toEqual({ email: 'odin@asgard.test', token: 'abc123' });
  });

  it('defaults email and token to empty strings when absent', () => {
    const result = load(loadEvent('http://localhost/account/reset-password'));

    expect(result).toEqual({ email: '', token: '' });
  });
});

describe('/account/reset-password +page.svelte', () => {
  const data = { email: 'odin@asgard.test', token: 'reset-token-123' };

  it('renders the data-testid="reset-password-form" form', () => {
    render(Page, { props: { data, form: null } });

    expect(screen.getByTestId('reset-password-form')).toBeInTheDocument();
  });

  it('renders new-password and confirm inputs with associated <label>s and autocomplete', () => {
    render(Page, { props: { data, form: null } });

    const password = screen.getByLabelText('New password');
    const confirm = screen.getByLabelText('Confirm new password');
    expect(password).toBeInTheDocument();
    expect(confirm).toBeInTheDocument();
    expect(password).toHaveAttribute('autocomplete', 'new-password');
    expect(confirm).toHaveAttribute('autocomplete', 'new-password');
  });

  it('seeds the hidden email/token fields from the loader data on first render', () => {
    const { container } = render(Page, { props: { data, form: null } });

    expect(hiddenValue(container, 'email')).toBe('odin@asgard.test');
    expect(hiddenValue(container, 'token')).toBe('reset-token-123');
  });

  it('renders no error alert when form is null', () => {
    render(Page, { props: { data, form: null } });

    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('maps error="password_mismatch" to "Passwords do not match."', () => {
    render(Page, { props: { data, form: { error: 'password_mismatch' } } });

    expect(screen.getByRole('alert')).toHaveTextContent('Passwords do not match.');
  });

  it('maps error="reset_unavailable" to the not-available copy', () => {
    render(Page, { props: { data, form: { error: 'reset_unavailable' } } });

    expect(screen.getByRole('alert')).toHaveTextContent(
      'Password reset is not currently available.',
    );
  });

  it('maps error="invalid_token" to the invalid/expired link copy', () => {
    render(Page, { props: { data, form: { error: 'invalid_token' } } });

    expect(screen.getByRole('alert')).toHaveTextContent(
      'This reset link is invalid or has expired. Request a new one.',
    );
  });

  it('prefers the form-echoed email/token over loader data on failure', () => {
    const { container } = render(Page, {
      props: {
        data,
        form: { error: 'invalid_token', email: 'echoed@asgard.test', token: 'echoed-token' },
      },
    });

    expect(hiddenValue(container, 'email')).toBe('echoed@asgard.test');
    expect(hiddenValue(container, 'token')).toBe('echoed-token');
  });

  it('sets aria-invalid on the password inputs when an error is present', () => {
    render(Page, { props: { data, form: { error: 'invalid_token' } } });

    expect(screen.getByLabelText('New password')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByLabelText('Confirm new password')).toHaveAttribute('aria-invalid', 'true');
  });

  it('omits aria-invalid when there is no error', () => {
    render(Page, { props: { data, form: null } });

    expect(screen.getByLabelText('New password')).not.toHaveAttribute('aria-invalid');
  });

  it('progressively enhances submission via use:enhance', () => {
    render(Page, { props: { data, form: null } });

    expect(enhance).toHaveBeenCalledTimes(1);
    const [node] = enhance.mock.calls[0] as unknown as [HTMLElement];
    expect(node).toBe(screen.getByTestId('reset-password-form'));
  });
});
