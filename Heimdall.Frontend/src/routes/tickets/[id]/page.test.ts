/**
 * Unit spec for the ticket edit page (`/tickets/[id]` +page.svelte).
 *
 * Prefilled from `data.ticket`; after a failed submit `form.values` win over the
 * loaded ticket and `form.errors` surface per field. `use:enhance` is mocked.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/svelte';

import { type Ticket, TicketStatus, TicketPriority } from '$lib/api/tickets';

const enhance = vi.fn(() => ({ destroy() {} }));

vi.mock('$app/forms', () => ({
  enhance: (...args: unknown[]) => enhance(...(args as [])),
}));

import Page from './+page.svelte';

function makeTicket(overrides: Partial<Ticket> = {}): Ticket {
  return {
    Id: 5,
    Title: 'Loaded title',
    Description: 'Loaded description',
    Status: TicketStatus.InProgress,
    Priority: TicketPriority.High,
    ProjectId: 'project-1',
    TeamId: 'team-1',
    ReporterId: 'reporter-1',
    AssigneeId: 'assignee-1',
    DateCreated: '2025-01-05T10:00:00Z',
    DateUpdated: '2025-01-06T10:00:00Z',
    ...overrides,
  };
}

afterEach(() => {
  vi.clearAllMocks();
});

describe('/tickets/[id] +page.svelte', () => {
  it('renders the data-testid="ticket-edit-form" form', () => {
    render(Page, { props: { data: { ticket: makeTicket() }, form: null } });

    expect(screen.getByTestId('ticket-edit-form')).toBeInTheDocument();
  });

  it('prefills every field from data.ticket', () => {
    render(Page, { props: { data: { ticket: makeTicket() }, form: null } });

    expect(screen.getByLabelText('Title')).toHaveValue('Loaded title');
    expect(screen.getByLabelText('Description')).toHaveValue('Loaded description');
    expect(screen.getByLabelText('Team')).toHaveValue('team-1');
    expect(screen.getByLabelText('Project')).toHaveValue('project-1');
    expect(screen.getByLabelText('Reporter')).toHaveValue('reporter-1');
  });

  it('selects the ticket Status and Priority in their selects', () => {
    render(Page, { props: { data: { ticket: makeTicket() }, form: null } });

    expect(screen.getByLabelText('Status')).toHaveValue(String(TicketStatus.InProgress));
    expect(screen.getByLabelText('Priority')).toHaveValue(String(TicketPriority.High));
  });

  it('prefers form.values over data.ticket after a failed submit', () => {
    render(Page, {
      props: {
        data: { ticket: makeTicket() },
        form: { errors: {}, values: { ...makeTicket(), Title: 'Edited title' } },
      },
    });

    expect(screen.getByLabelText('Title')).toHaveValue('Edited title');
  });

  it('renders field-level errors from form.errors', () => {
    render(Page, {
      props: {
        data: { ticket: makeTicket() },
        form: { errors: { Title: ['Title is required.'] }, values: makeTicket() },
      },
    });

    expect(screen.getByText('Title is required.')).toBeInTheDocument();
    expect(screen.getByLabelText('Title')).toHaveAttribute('aria-invalid', 'true');
  });

  it('renders a form-level error for the "" error key', () => {
    render(Page, {
      props: {
        data: { ticket: makeTicket() },
        form: {
          errors: { '': ['Unable to save the ticket. Please try again.'] },
          values: makeTicket(),
        },
      },
    });

    expect(screen.getByRole('alert')).toHaveTextContent(
      'Unable to save the ticket. Please try again.',
    );
  });

  it('renders the assignee field empty when AssigneeId is null', () => {
    render(Page, { props: { data: { ticket: makeTicket({ AssigneeId: null }) }, form: null } });

    expect(screen.getByLabelText(/Assignee/)).toHaveValue('');
  });

  it('progressively enhances submission via use:enhance', () => {
    render(Page, { props: { data: { ticket: makeTicket() }, form: null } });

    expect(enhance).toHaveBeenCalledTimes(1);
  });
});
