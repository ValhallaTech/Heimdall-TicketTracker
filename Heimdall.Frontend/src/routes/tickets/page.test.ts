/**
 * Unit spec for the tickets list page (`/tickets` +page.svelte).
 *
 * Renders rows through the shared TicketQueueTable plus a GET search/sort
 * toolbar and pagination controls (GET forms). Driven by the `data` prop
 * ({ tickets: PagedResult, query: TicketsQuery }). Both empty-state branches
 * (no tickets / no search match) and the pagination disabled-edge branches are
 * exercised.
 */
import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/svelte';

import Page from './+page.svelte';
import { type PagedResult, type Ticket, TicketStatus, TicketPriority } from '$lib/api/tickets';

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

function page(items: Ticket[], over: Partial<PagedResult<Ticket>> = {}): PagedResult<Ticket> {
  return {
    Items: items,
    TotalCount: items.length,
    Page: 1,
    PageSize: 25,
    TotalPages: 1,
    ...over,
  };
}

const defaultQuery = {
  page: 1,
  pageSize: 25,
  searchText: '',
  sortField: 'DateCreated',
  sortDirection: 'Descending',
};

describe('/tickets +page.svelte', () => {
  it('renders the data-testid="tickets-page" landmark', () => {
    render(Page, { props: { data: { tickets: page([makeTicket()]), query: defaultQuery } } });

    expect(screen.getByTestId('tickets-page')).toBeInTheDocument();
  });

  it('renders the shared TicketQueueTable with the page items', () => {
    render(Page, {
      props: {
        data: { tickets: page([makeTicket({ Id: 3, Title: 'Listed' })]), query: defaultQuery },
      },
    });

    expect(screen.getByTestId('ticket-queue-table')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Listed' })).toHaveAttribute('href', '/tickets/3');
  });

  it('renders a "New ticket" link to /tickets/new', () => {
    render(Page, { props: { data: { tickets: page([makeTicket()]), query: defaultQuery } } });

    expect(screen.getAllByRole('link', { name: 'New ticket' })[0]).toHaveAttribute(
      'href',
      '/tickets/new',
    );
  });

  it('renders the search + sort GET form with current query values', () => {
    render(Page, {
      props: {
        data: {
          tickets: page([makeTicket()]),
          query: { ...defaultQuery, searchText: 'down', sortField: 'Title' },
        },
      },
    });

    expect(screen.getByLabelText('Search')).toHaveValue('down');
    expect(screen.getByLabelText('Sort by')).toHaveValue('Title');
  });

  it('renders the empty-state "No tickets yet" copy when total is 0 and no search', () => {
    render(Page, {
      props: { data: { tickets: page([], { TotalCount: 0 }), query: defaultQuery } },
    });

    expect(screen.getByRole('heading', { level: 2, name: 'No tickets yet' })).toBeInTheDocument();
  });

  it('renders the "No tickets found" copy + clear-search link when a search yielded 0', () => {
    render(Page, {
      props: {
        data: {
          tickets: page([], { TotalCount: 0 }),
          query: { ...defaultQuery, searchText: 'zzz' },
        },
      },
    });

    expect(screen.getByRole('heading', { level: 2, name: 'No tickets found' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Clear search' })).toHaveAttribute('href', '/tickets');
  });

  it('renders no pagination nav when TotalPages <= 1', () => {
    render(Page, {
      props: { data: { tickets: page([makeTicket()], { TotalPages: 1 }), query: defaultQuery } },
    });

    expect(screen.queryByRole('navigation', { name: 'Pagination' })).not.toBeInTheDocument();
  });

  it('disables Previous on the first page', () => {
    render(Page, {
      props: {
        data: {
          tickets: page([makeTicket()], { TotalCount: 60, TotalPages: 3, Page: 1 }),
          query: defaultQuery,
        },
      },
    });

    expect(screen.getByText('Previous')).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByRole('button', { name: 'Next page' })).toBeInTheDocument();
  });

  it('disables Next on the last page', () => {
    render(Page, {
      props: {
        data: {
          tickets: page([makeTicket()], { TotalCount: 60, TotalPages: 3, Page: 3 }),
          query: { ...defaultQuery, page: 3 },
        },
      },
    });

    expect(screen.getByText('Next')).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByRole('button', { name: 'Previous page' })).toBeInTheDocument();
  });
});
