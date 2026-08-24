import { screen, within } from '@testing-library/react';
import { Route, Routes } from 'react-router';
import { afterEach, describe, expect, it } from 'vitest';
import { CustomerDetailPage } from './customer-detail-page';
import { stubFetch, type FetchStub, type StubbedResponse } from '@/test/api-stub';
import { customer, serviceAccount, serviceLocation } from '@/test/registry-fixtures';
import { renderWithProviders } from '@/test/render';

const record = customer();
const premise = serviceLocation();
const otherPremise = serviceLocation({
  id: '0192f000-0000-7000-8000-000000000102',
  locationCode: 'L-000002',
  formattedAddress: '8 Sinapalo Road, Sinapalo, Rota, MP 96951',
  address: { ...serviceLocation().address, line1: '8 Sinapalo Road', city: 'Sinapalo' },
});

const open = serviceAccount();
const closed = serviceAccount({
  id: '0192f000-0000-7000-8000-000000000202',
  accountNumber: 'A-000002',
  serviceLocationId: otherPremise.id,
  status: 'Closed',
  allowedTransitions: [],
  serviceEndedAt: '2026-01-09T00:30:00+00:00',
  statusReason: 'Moved off island',
  history: [],
});

let stub: FetchStub;

afterEach(() => stub?.restore());

/** The 360 is three services' worth of rows: the customer, its accounts, and each premise by id. */
function fullWorld(url: URL): StubbedResponse | undefined {
  if (url.pathname === `/api/customers/${record.id}`) return { body: record };
  if (url.pathname === '/api/service-accounts') return { body: [closed, open] };
  if (url.pathname === `/api/service-locations/${premise.id}`) return { body: premise };
  if (url.pathname === `/api/service-locations/${otherPremise.id}`) return { body: otherPremise };
  return undefined;
}

function renderPage(respond: (url: URL) => StubbedResponse | undefined = fullWorld) {
  stub = stubFetch(respond);

  return renderWithProviders(
    <Routes>
      <Route path="/customers/:customerId" element={<CustomerDetailPage />} />
    </Routes>,
    { route: `/customers/${record.id}` },
  );
}

describe('CustomerDetailPage', () => {
  it('shows the customer record', async () => {
    renderPage();

    expect(await screen.findByRole('heading', { name: 'Songsong Bakery', level: 2 })).toBeInTheDocument();
    expect(screen.getByText('C-000001')).toBeInTheDocument();
    expect(screen.getByText('Maria Taimanao')).toBeInTheDocument();
    expect(screen.getByText('$450.00')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'maria@songsong-bakery.test' })).toHaveAttribute(
      'href',
      'mailto:maria@songsong-bakery.test',
    );
  });

  /** The fan-out this page exists for: customer → service accounts → the premise each is served at. */
  it('fans out into service accounts and resolves each premise', async () => {
    renderPage();

    expect(await screen.findByText('A-000001')).toBeInTheDocument();
    expect(screen.getByText('A-000002')).toBeInTheDocument();
    expect(
      screen.getByRole('link', { name: '12 Songsong Village Road, Songsong, Rota, MP 96951' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('link', { name: '8 Sinapalo Road, Sinapalo, Rota, MP 96951' })).toBeInTheDocument();
  });

  /** Cross-module rows are reached through the owning service — never a join, never a table read. */
  it('asks the accounts service for this customer rather than listing everything', async () => {
    renderPage();
    await screen.findByText('A-000001');

    expect(stub.lastCall('/api/service-accounts')?.searchParams.get('customerId')).toBe(record.id);
  });

  /** Each premise is fetched by id, so a capped list page can never silently drop one. */
  it('fetches each premise by id, once per distinct premise', async () => {
    renderPage();
    await screen.findByText('A-000001');

    const premiseCalls = stub.calls.filter((url) => url.pathname.startsWith('/api/service-locations/'));
    expect(new Set(premiseCalls.map((url) => url.pathname)).size).toBe(2);
  });

  it('puts the open account above the closed one', async () => {
    renderPage();
    await screen.findByText('A-000001');

    const numbers = screen
      .getAllByText(/^A-0000\d\d$/)
      .map((node) => node.textContent);

    expect(numbers).toEqual(['A-000001', 'A-000002']);
  });

  it('renders the account history newest first, with the opening line named', async () => {
    renderPage();
    await screen.findByText('A-000001');

    const entries = screen.getAllByRole('listitem');
    const timeline = entries.map((entry) => entry.textContent);

    expect(timeline.some((text) => text?.includes('Pending → Active'))).toBe(true);
    expect(timeline.some((text) => text?.includes('Account opened'))).toBe(true);
    // Newest first: the transition comes before the opening line.
    const transition = timeline.findIndex((text) => text?.includes('Pending → Active'));
    const opened = timeline.findIndex((text) => text?.includes('Account opened'));
    expect(transition).toBeLessThan(opened);
  });

  it('shows what the state machine would still allow, and says when nothing is left', async () => {
    renderPage();
    await screen.findByText('A-000002');

    // The open account may be disconnected or closed; the closed one is terminal.
    expect(screen.getAllByText('Disconnected').length).toBeGreaterThan(0);
    expect(screen.getByText(/closed is terminal/i)).toBeInTheDocument();
  });

  /** Failure path: a customer id that no longer resolves is a 404, not a blank page. */
  it('reports a customer that does not exist', async () => {
    renderPage((url) =>
      url.pathname === `/api/customers/${record.id}`
        ? { status: 404, body: { title: 'Not found', status: 404, detail: 'No customer with that id.' } }
        : { body: [] },
    );

    expect(await screen.findByRole('alert')).toHaveTextContent('That did not load');
    expect(screen.getByText('No customer with that id.')).toBeInTheDocument();
  });

  /** Failure path: a customer with no accounts is a real state — a prospect nobody has connected. */
  it('shows an empty state when the customer holds no accounts', async () => {
    renderPage((url) => {
      if (url.pathname === `/api/customers/${record.id}`) return { body: record };
      if (url.pathname === '/api/service-accounts') return { body: [] };
      return undefined;
    });

    expect(await screen.findByText('No service accounts yet')).toBeInTheDocument();
  });

  /** The accounts query can fail on its own; the customer record above it must survive that. */
  it('keeps the customer record when the accounts query fails', async () => {
    renderPage((url) => {
      if (url.pathname === `/api/customers/${record.id}`) return { body: record };
      if (url.pathname === '/api/service-accounts') return { status: 500, body: { title: 'Server error', status: 500 } };
      return undefined;
    });

    expect(await screen.findByRole('heading', { name: 'Songsong Bakery', level: 2 })).toBeInTheDocument();
    expect(within(await screen.findByRole('alert')).getByText(/did not load/i)).toBeInTheDocument();
  });
});
