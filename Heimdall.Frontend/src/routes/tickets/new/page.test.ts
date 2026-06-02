/**
 * Unit spec for the new ticket page (`/tickets/new` +page.svelte).
 *
 * Progressively enhanced form (`use:enhance` mocked) populated from the label
 * maps. Field-level errors (`form.errors`), the form-level "" error key, and
 * value repopulation (`form.values`) are driven through the `form` prop.
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

describe('/tickets/new +page.svelte', () => {
  it('renders the data-testid="ticket-new-form" form', () => {
    render(Page, { props: { form: null } });

    expect(screen.getByTestId('ticket-new-form')).toBeInTheDocument();
  });

  it('renders title, description, team, and project fields with <label>s', () => {
    render(Page, { props: { form: null } });

    expect(screen.getByLabelText('Title')).toBeInTheDocument();
    expect(screen.getByLabelText('Description')).toBeInTheDocument();
    expect(screen.getByLabelText('Team')).toBeInTheDocument();
    expect(screen.getByLabelText('Project')).toBeInTheDocument();
  });

  it('populates Status and Priority selects from the label maps', () => {
    render(Page, { props: { form: null } });

    const status = screen.getByLabelText('Status');
    const priority = screen.getByLabelText('Priority');
    expect(status).toBeInTheDocument();
    expect(priority).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'In Progress' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'Critical' })).toBeInTheDocument();
  });

  it('progressively enhances submission via use:enhance', () => {
    render(Page, { props: { form: null } });

    expect(enhance).toHaveBeenCalledTimes(1);
  });

  it('renders field-level errors from form.errors next to each field', () => {
    render(Page, { props: { form: { errors: { Title: ['Title is required.'] }, values: null } } });

    expect(screen.getByText('Title is required.')).toBeInTheDocument();
    expect(screen.getByLabelText('Title')).toHaveAttribute('aria-invalid', 'true');
  });

  it('renders a form-level error for the "" error key', () => {
    render(Page, {
      props: {
        form: { errors: { '': ['Unable to create the ticket. Please try again.'] }, values: null },
      },
    });

    expect(screen.getByRole('alert')).toHaveTextContent(
      'Unable to create the ticket. Please try again.',
    );
  });

  it('repopulates fields from form.values after a failed submit', () => {
    render(Page, {
      props: {
        form: {
          errors: {},
          values: { Title: 'Kept title', Description: 'Kept desc', TeamId: 't', ProjectId: 'p' },
        },
      },
    });

    expect(screen.getByLabelText('Title')).toHaveValue('Kept title');
    expect(screen.getByLabelText('Team')).toHaveValue('t');
    expect(screen.getByLabelText('Project')).toHaveValue('p');
  });

  it('renders a Cancel link back to /tickets', () => {
    render(Page, { props: { form: null } });

    expect(screen.getByRole('link', { name: 'Cancel' })).toHaveAttribute('href', '/tickets');
  });
});
