/**
 * Unit spec for the shared TicketQueueTable component
 * (`lib/components/TicketQueueTable.svelte`, Phase 6.4).
 *
 * Purely presentational, so the test renders it directly with `tickets` props
 * and asserts on the user-visible surface: the landmark, one row per ticket, the
 * title link target, the status/priority badge labels, the assignee (with the
 * em-dash fallback), the formatted created date, and both empty-state branches.
 */
import { describe, expect, it } from 'vitest';
import { render, screen, within } from '@testing-library/svelte';

import TicketQueueTable from './TicketQueueTable.svelte';
import { type Ticket, TicketStatus, TicketPriority } from '$lib/api/tickets';

function makeTicket(overrides: Partial<Ticket> = {}): Ticket {
  return {
    Id: 1,
    Title: 'Bifrost is down',
    Description: 'desc',
    Status: TicketStatus.Open,
    Priority: TicketPriority.Low,
    ProjectId: 'project-1',
    TeamId: 'team-1',
    ReporterId: 'reporter-1',
    AssigneeId: 'assignee-1',
    DateCreated: '2025-01-05T10:00:00Z',
    DateUpdated: '2025-01-06T10:00:00Z',
    ...overrides,
  };
}

describe('TicketQueueTable.svelte', () => {
  it('renders the data-testid="ticket-queue-table" landmark', () => {
    render(TicketQueueTable, { props: { tickets: [makeTicket()] } });

    expect(screen.getByTestId('ticket-queue-table')).toBeInTheDocument();
  });

  it('renders one row per ticket with a title link to /tickets/{Id}', () => {
    render(TicketQueueTable, {
      props: {
        tickets: [makeTicket({ Id: 11, Title: 'First' }), makeTicket({ Id: 22, Title: 'Second' })],
      },
    });

    const first = screen.getByRole('link', { name: 'First' });
    const second = screen.getByRole('link', { name: 'Second' });
    expect(first).toHaveAttribute('href', '/tickets/11');
    expect(second).toHaveAttribute('href', '/tickets/22');
    expect(screen.getAllByRole('row')).toHaveLength(3); // header + 2 rows
  });

  it('renders status and priority badges via the label maps', () => {
    render(TicketQueueTable, {
      props: {
        tickets: [
          makeTicket({ Status: TicketStatus.InProgress, Priority: TicketPriority.Critical }),
        ],
      },
    });

    expect(screen.getByText('In Progress')).toBeInTheDocument();
    expect(screen.getByText('Critical')).toBeInTheDocument();
  });

  it('renders the assignee when present', () => {
    render(TicketQueueTable, { props: { tickets: [makeTicket({ AssigneeId: 'heimdall' })] } });

    expect(screen.getByText('heimdall')).toBeInTheDocument();
  });

  it('falls back to an em dash when the ticket is unassigned', () => {
    render(TicketQueueTable, { props: { tickets: [makeTicket({ AssigneeId: null })] } });

    expect(screen.getByText('—')).toBeInTheDocument();
  });

  it('formats the created date as a localised short date', () => {
    render(TicketQueueTable, {
      props: { tickets: [makeTicket({ DateCreated: '2025-01-05T10:00:00Z' })] },
    });

    expect(screen.getByText('Jan 5, 2025')).toBeInTheDocument();
  });

  it('falls back to the raw value for an unparseable created date', () => {
    render(TicketQueueTable, { props: { tickets: [makeTicket({ DateCreated: 'not-a-date' })] } });

    expect(screen.getByText('not-a-date')).toBeInTheDocument();
  });

  it('renders the default empty message when tickets is empty', () => {
    render(TicketQueueTable, { props: { tickets: [] } });

    const table = screen.getByTestId('ticket-queue-table');
    expect(within(table).getByText('No tickets in this queue.')).toBeInTheDocument();
    expect(screen.queryByRole('link')).not.toBeInTheDocument();
  });

  it('renders a custom emptyMessage prop when provided', () => {
    render(TicketQueueTable, {
      props: { tickets: [], emptyMessage: 'No tickets are routed to this team.' },
    });

    expect(screen.getByText('No tickets are routed to this team.')).toBeInTheDocument();
  });
});
