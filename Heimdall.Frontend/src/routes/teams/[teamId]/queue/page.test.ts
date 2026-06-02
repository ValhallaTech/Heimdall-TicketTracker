/**
 * Unit spec for the team queue page (`/teams/[teamId]/queue` +page.svelte).
 *
 * Renders the shared TicketQueueTable with the team's tickets plus a heading
 * showing the team id; the empty state is delegated to the table's
 * team-specific `emptyMessage`. Driven entirely by the `data` prop.
 */
import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/svelte';

import Page from './+page.svelte';
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
    AssigneeId: null,
    DateCreated: '2025-01-05T10:00:00Z',
    DateUpdated: '2025-01-06T10:00:00Z',
    ...overrides,
  };
}

describe('/teams/[teamId]/queue +page.svelte', () => {
  it('renders the data-testid="team-queue" section', () => {
    render(Page, { props: { data: { teamId: 'team-1', tickets: [] } } });

    expect(screen.getByTestId('team-queue')).toBeInTheDocument();
  });

  it('shows the team id in the heading area', () => {
    render(Page, { props: { data: { teamId: 'team-xyz', tickets: [] } } });

    expect(screen.getByRole('heading', { level: 1, name: 'Team queue' })).toBeInTheDocument();
    expect(screen.getByText('team-xyz')).toBeInTheDocument();
  });

  it('renders the shared TicketQueueTable with the team tickets', () => {
    render(Page, {
      props: { data: { teamId: 'team-1', tickets: [makeTicket({ Id: 9, Title: 'Routed' })] } },
    });

    expect(screen.getByTestId('ticket-queue-table')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Routed' })).toHaveAttribute('href', '/tickets/9');
  });

  it('renders the team-specific empty message when there are no tickets', () => {
    render(Page, { props: { data: { teamId: 'team-1', tickets: [] } } });

    expect(screen.getByText('No tickets are routed to this team.')).toBeInTheDocument();
  });
});
