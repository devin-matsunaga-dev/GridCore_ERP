import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { Route, Routes } from 'react-router';
import { CustomersPage } from './customers-page';
import { stubFetch, type FetchStub } from '@/test/api-stub';
import { customer } from '@/test/registry-fixtures';
import { renderWithProviders } from '@/test/render';

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
});
