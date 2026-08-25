import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { Route, Routes } from 'react-router';
import { CustomersPage } from './customers-page';
import { stubFetch, type FetchStub } from '@/test/api-stub';
import { customer } from '@/test/registry-fixtures';
import { renderWithProviders } from '@/test/render';
import type { CustomerSearchHit } from '@/api/customers';

const rows = [
  customer(),
  customer({
    id: '0192f000-0000-7000-8000-000000000002',
    accountNumber: 'C-000002',
    name: 'Alfonso Cruz',
    contactName: null,
    email: null,
    phone: null,
    class: 'Residential',
    status: 'Suspended',
    depositHeld: 75,
  }),
];

/** The search endpoint's answer, in the shape the registry table renders rows out of. */
function searchAnswering(hits: CustomerSearchHit[]) {
  return (url: URL) => {
    if (url.pathname !== '/api/customers/search') return undefined;

    return {
      body: {
        term: url.searchParams.get('q') ?? '',
        kinds: ['Name', 'Address'],
        hits,
        total: hits.length,
        page: 1,
        pageSize: 200,
        truncated: false,
      },
    };
  };
}

function hit(overrides: Partial<CustomerSearchHit> = {}): CustomerSearchHit {
  return {
    customer: rows[0],
    matchedOn: 'Phone',
    isExact: true,
    matchedValue: '670-285-1234',
    serviceAccountCount: 1,
    serviceAccountNumber: 'A-000012',
    serviceAddress: '12 Beach St, Songsong, Rota',
    meterNumber: null,
    ...overrides,
  };
}

let stub: FetchStub;

afterEach(() => stub?.restore());

function renderPage(respond?: (url: URL) => { status?: number; body?: unknown } | undefined) {
  stub = stubFetch(
    respond ?? ((url) => (url.pathname === '/api/customers' ? { body: rows } : undefined)),
  );

  return renderWithProviders(<CustomersPage />, { route: '/customers' });
}

describe('CustomersPage', () => {
  it('lists the registry', async () => {
    renderPage();

    expect(await screen.findByText('Songsong Bakery')).toBeInTheDocument();
    expect(screen.getByText('Alfonso Cruz')).toBeInTheDocument();
    expect(screen.getByText('C-000001')).toBeInTheDocument();
    expect(screen.getByText('2 shown')).toBeInTheDocument();
  });

  it('renders the customer status through the shared semantic map', async () => {
    renderPage();

    await screen.findByText('Songsong Bakery');

    // Scoped to the table: "Active" and "Suspended" are also options in the status filter above it.
    const table = within(screen.getByRole('table', { name: 'Customers' }));
    // `bg-success-soft` is the success pill from DESIGN.md; Suspended is a warning.
    expect(table.getByText('Active')).toHaveClass('bg-success-soft');
    expect(table.getByText('Suspended')).toHaveClass('bg-warning-soft');
  });

  /**
   * The filters are server-side, so what matters is the request, not the rendered array: a select
   * that stopped reaching the host would still look right on screen while showing the wrong rows.
   */
  it('sends the status filter to the host rather than filtering in the browser', async () => {
    renderPage();
    await screen.findByText('Songsong Bakery');

    await userEvent.selectOptions(screen.getByLabelText('Status'), 'Suspended');

    await waitFor(() => {
      expect(stub.lastCall('/api/customers')?.searchParams.get('status')).toBe('Suspended');
    });
  });

  it('asks for the whole window and never sends an empty filter', async () => {
    renderPage();
    await screen.findByText('Songsong Bakery');

    const request = stub.lastCall('/api/customers')!;
    expect(request.searchParams.get('limit')).toBe('200');
    // An empty select is "no filter", not `?status=` — which the host would try to parse as an enum.
    expect(request.searchParams.has('status')).toBe(false);
    expect(request.searchParams.has('class')).toBe(false);
  });

  it('sorts on a column without going back to the server', async () => {
    renderPage();
    await screen.findByText('Songsong Bakery');
    const before = stub.calls.length;

    await userEvent.click(screen.getByRole('button', { name: /name/i }));

    // Rows in document order, header dropped: ascending by name puts Alfonso above Songsong.
    const [, ...body] = within(screen.getByRole('table', { name: 'Customers' })).getAllByRole('row');
    expect(body[0]).toHaveTextContent('Alfonso Cruz');
    expect(body[1]).toHaveTextContent('Songsong Bakery');
    // No sort parameter exists on the endpoint, so ordering must not have cost a request.
    expect(stub.calls.length).toBe(before);
  });

  it('opens the 360 page for the row that was activated', async () => {
    stub = stubFetch((url) => (url.pathname === '/api/customers' ? { body: rows } : undefined));

    renderWithProviders(
      <Routes>
        <Route path="/customers" element={<CustomersPage />} />
        <Route path="/customers/:customerId" element={<p>Customer 360</p>} />
      </Routes>,
      { route: '/customers' },
    );

    await screen.findByText('Songsong Bakery');
    await userEvent.click(screen.getByRole('button', { name: /Songsong Bakery/ }));

    // The row navigates to the detail route rather than opening a drawer — a customer fans out
    // into accounts, premises and (from WP-2.x) meters and bills, which is more than a panel holds.
    expect(await screen.findByText('Customer 360')).toBeInTheDocument();
  });

  /** Failure path: an empty registry is an empty state, not a bare table. */
  it('shows an empty state when nothing matches', async () => {
    renderPage((url) => (url.pathname === '/api/customers' ? { body: [] } : undefined));

    expect(await screen.findByText('No customers registered yet')).toBeInTheDocument();
  });

  /** Failure path: the RBAC gate's 403 reads as a permission message, not an empty registry. */
  it('reports a permission refusal', async () => {
    renderPage((url) =>
      url.pathname === '/api/customers'
        ? { status: 403, body: { title: 'Forbidden', status: 403, detail: 'You do not hold customers.read.' } }
        : undefined,
    );

    expect(await screen.findByRole('alert')).toHaveTextContent('You do not have access to this');
    expect(screen.getByText('You do not hold customers.read.')).toBeInTheDocument();
  });

  /**
   * WP-2.9. The registry's search field IS the CSR search — one box, not a second screen beside it.
   * An empty box lists the registry; a term in it runs the five-kind search over the same field, so
   * a rep who types a phone number gets an answer rather than an empty table.
   */
  describe('the search field', () => {
    it('lists the registry while the box is empty and asks the search endpoint for nothing', async () => {
      renderPage();
      await screen.findByText('Songsong Bakery');

      expect(stub.lastCall('/api/customers')).toBeDefined();
      expect(stub.lastCall('/api/customers/search')).toBeUndefined();
    });

    it('runs the search once there is a term, instead of the plain list', async () => {
      renderPage((url) => searchAnswering([hit()])(url) ?? (url.pathname === '/api/customers' ? { body: rows } : undefined));
      await screen.findByText('Songsong Bakery');

      await userEvent.type(screen.getByLabelText('Search customers'), '670-285-1234');

      await waitFor(() => expect(stub.lastCall('/api/customers/search')).toBeDefined());

      const request = stub.lastCall('/api/customers/search')!;

      expect(request.searchParams.get('q')).toBe('670-285-1234');
      // One window, sorted and paged in the browser, exactly as the plain list is.
      expect(request.searchParams.get('pageSize')).toBe('200');
    });

    it('finds a customer by their contact number', async () => {
      // The thing the old registry filter could never do: it matched account number and name only,
      // while its placeholder promised email. A phone number now answers.
      renderPage((url) => searchAnswering([hit()])(url) ?? (url.pathname === '/api/customers' ? { body: rows } : undefined));
      await screen.findByText('Songsong Bakery');

      await userEvent.type(screen.getByLabelText('Search customers'), '670-285-1234');

      const table = within(screen.getByRole('table', { name: 'Customers' }));

      expect(await table.findByText('Exact phone')).toBeInTheDocument();
      expect(table.getByText('670-285-1234')).toBeInTheDocument();
    });

    it('carries the status and class selects into the search', async () => {
      // The box sits beside the selects, so a search that ignored them would answer a question
      // nobody asked.
      renderPage((url) => searchAnswering([hit()])(url) ?? (url.pathname === '/api/customers' ? { body: rows } : undefined));
      await screen.findByText('Songsong Bakery');

      await userEvent.type(screen.getByLabelText('Search customers'), 'cruz');
      await userEvent.selectOptions(screen.getByLabelText('Status'), 'Suspended');

      await waitFor(() =>
        expect(stub.lastCall('/api/customers/search')?.searchParams.get('status')).toBe('Suspended'),
      );
    });

    it('explains each row only while a search is what produced it', async () => {
      renderPage((url) => searchAnswering([hit()])(url) ?? (url.pathname === '/api/customers' ? { body: rows } : undefined));
      await screen.findByText('Songsong Bakery');

      // No column for it on a plain listing: there is no reason to explain a row nobody searched for.
      expect(screen.queryByRole('button', { name: /matched on/i })).not.toBeInTheDocument();

      await userEvent.type(screen.getByLabelText('Search customers'), 'cruz');

      expect(await screen.findByRole('button', { name: /matched on/i })).toBeInTheDocument();
    });

    it('keeps every registry column when it switches to search results', async () => {
      // A rep watching the table as they type sees the columns they were reading a moment ago with
      // one more beside them, not a different table.
      renderPage((url) => searchAnswering([hit()])(url) ?? (url.pathname === '/api/customers' ? { body: rows } : undefined));
      await screen.findByText('Songsong Bakery');

      await userEvent.type(screen.getByLabelText('Search customers'), 'cruz');

      const table = within(screen.getByRole('table', { name: 'Customers' }));

      await table.findByText('Exact phone');
      expect(table.getByText('C-000001')).toBeInTheDocument();
      expect(table.getByText('Active')).toHaveClass('bg-success-soft');
    });

    it('reaches a customer with two keys from the box', async () => {
      // Type, Down, Enter — WP-2.9's keyboard-first lookup, on the registry table.
      stub = stubFetch(
        (url) => searchAnswering([hit()])(url) ?? (url.pathname === '/api/customers' ? { body: rows } : undefined),
      );

      renderWithProviders(
        <Routes>
          <Route path="/customers" element={<CustomersPage />} />
          <Route path="/customers/:customerId" element={<p>Customer 360</p>} />
        </Routes>,
        { route: '/customers' },
      );

      await screen.findByText('Songsong Bakery');

      const box = screen.getByLabelText('Search customers');
      await userEvent.type(box, 'cruz');
      await within(screen.getByRole('table', { name: 'Customers' })).findByText('Exact phone');

      await userEvent.type(box, '{ArrowDown}');
      await userEvent.keyboard('{Enter}');

      expect(await screen.findByText('Customer 360')).toBeInTheDocument();
    });

    /** Failure path: a search that matched nobody says so, and says what it looked for. */
    it('shows a no-match empty state naming the term', async () => {
      renderPage((url) => searchAnswering([])(url) ?? (url.pathname === '/api/customers' ? { body: rows } : undefined));
      await screen.findByText('Songsong Bakery');

      await userEvent.type(screen.getByLabelText('Search customers'), 'nobody');

      expect(await screen.findByText(/Nothing in the register matches "nobody"/)).toBeInTheDocument();
      expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    });

    /** Failure path: a refused search is the RBAC gate, not an empty result. */
    it('reports a permission refusal from the search endpoint', async () => {
      renderPage((url) =>
        url.pathname === '/api/customers/search'
          ? { status: 403, body: { title: 'Forbidden', status: 403, detail: 'You do not hold customers.read.' } }
          : url.pathname === '/api/customers'
            ? { body: rows }
            : undefined,
      );

      await screen.findByText('Songsong Bakery');
      await userEvent.type(screen.getByLabelText('Search customers'), 'cruz');

      expect(await screen.findByRole('alert')).toHaveTextContent('You do not have access to this');
    });
  });
});
