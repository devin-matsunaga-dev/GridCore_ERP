import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router';
import { afterEach, describe, expect, it } from 'vitest';
import type { ServiceAccount } from '@/api/customers';
import { CustomerDetailPage } from './customer-detail-page';
import { stubFetch, type FetchStub, type StubbedResponse } from '@/test/api-stub';
import { customer, meter, serviceAccount, serviceLocation } from '@/test/registry-fixtures';
import { bill, payment } from '@/test/revenue-cycle-fixtures';
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

/** The meter on the open account's premise. The closed account's premise has none — its tenant left. */
const fitted = meter();

/** An issued bill and the payment that settled it — the demonstration figures, $63.62. */
const issued = bill();
const settled = payment();

let stub: FetchStub;

afterEach(() => stub?.restore());

/** A list row as the register sends one: everything the account has, and none of its transitions. */
function withoutHistory(account: ServiceAccount): ServiceAccount {
  return Object.assign({}, account, { history: [] });
}

/**
 * The whole 360, as five services answer it: the customer, its accounts, each account's transitions,
 * each premise by id, the meter measuring each premise, this customer's bills and its payments.
 *
 * The history is its OWN route on purpose. `GET /api/service-accounts` returns rows with no history
 * on them, so a fixture that folded it into the list row would be testing a response the host does
 * not send.
 */
function fullWorld(url: URL): StubbedResponse | undefined {
  if (url.pathname === `/api/customers/${record.id}`) return { body: record };
  if (url.pathname === '/api/service-accounts') {
    return { body: [withoutHistory(closed), withoutHistory(open)] };
  }
  if (url.pathname === `/api/service-accounts/${open.id}/history`) return { body: open.history };
  if (url.pathname === `/api/service-accounts/${closed.id}/history`) return { body: [] };
  if (url.pathname === `/api/service-locations/${premise.id}`) return { body: premise };
  if (url.pathname === `/api/service-locations/${otherPremise.id}`) return { body: otherPremise };
  if (url.pathname === '/api/bills') return { body: [issued] };
  if (url.pathname === '/api/payments') return { body: [settled] };

  if (url.pathname === '/api/meters') {
    return { body: url.searchParams.get('serviceLocationId') === premise.id ? [fitted] : [] };
  }

  return undefined;
}

/** The customer with nothing behind them: a prospect nobody has connected, billed or paid. */
function bareWorld(url: URL): StubbedResponse | undefined {
  if (url.pathname === `/api/customers/${record.id}`) return { body: record };
  if (url.pathname === '/api/service-accounts') return { body: [] };
  if (url.pathname === '/api/bills') return { body: [] };
  if (url.pathname === '/api/payments') return { body: [] };

  return undefined;
}

/** Both routes the 360 answers on, so a test can land on a tab or walk to one. */
function renderPage(
  respond: (url: URL) => StubbedResponse | undefined = fullWorld,
  route = `/customers/${record.id}`,
) {
  stub = stubFetch(respond);

  return renderWithProviders(
    <Routes>
      <Route path="/customers/:customerId" element={<CustomerDetailPage />} />
      <Route path="/customers/:customerId/:tab" element={<CustomerDetailPage />} />
    </Routes>,
    { route },
  );
}

/** Waits for the customer to resolve, which is what puts the tab strip and the summary on screen. */
async function pageReady(): Promise<void> {
  await screen.findByRole('heading', { name: 'Songsong Bakery', level: 2 });
}

/**
 * The status pills in a table, by their slot rather than by their text.
 *
 * "Issued" is both a column header and a bill status, and "Approved" is both a payment status and
 * what the provider said — so matching on the words alone finds two nodes and asserts nothing about
 * which was meant.
 */
function statusPills(table: HTMLElement): string[] {
  return [...table.querySelectorAll('[data-slot="status-pill"]')].map((pill) => pill.textContent ?? '');
}

/** Clicks a tab the way a rep would, rather than re-rendering at its URL. */
async function openTab(label: string): Promise<void> {
  await pageReady();
  await userEvent.click(screen.getByRole('link', { name: label }));
}

describe('CustomerDetailPage', () => {
  it('opens on the summary, with the tab strip beside it', async () => {
    renderPage();
    await pageReady();

    for (const label of ['Summary', 'Bills', 'Payments', 'Timeline', 'Work orders']) {
      expect(screen.getByRole('link', { name: label })).toBeInTheDocument();
    }

    // The summary's own contents, and none of the other tabs'.
    expect(screen.getByRole('heading', { name: 'Customer record' })).toBeInTheDocument();
    expect(await screen.findByRole('table', { name: 'Service accounts' })).toBeInTheDocument();
    expect(screen.queryByRole('table', { name: 'Bills' })).not.toBeInTheDocument();
    expect(screen.queryByRole('table', { name: 'Payments' })).not.toBeInTheDocument();
  });

  it('shows the customer record', async () => {
    renderPage();
    await pageReady();

    expect(screen.getByText('C-000001')).toBeInTheDocument();
    expect(screen.getByText('Maria Taimanao')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'maria@songsong-bakery.test' })).toHaveAttribute(
      'href',
      'mailto:maria@songsong-bakery.test',
    );
  });

  it('shows what the customer owes, worked out from the bills', async () => {
    renderPage();
    await pageReady();

    // One issued bill of $63.62, nothing paid against it yet.
    expect(screen.getByText('Current balance')).toBeInTheDocument();
    expect(await screen.findByText('1 bill open')).toBeInTheDocument();
    expect(screen.getAllByText('$63.62').length).toBeGreaterThan(0);
  });

  /** The fan-out this page exists for: customer → service accounts → the premise each is served at. */
  it('lists the service accounts as a table, each with its premise and meter', async () => {
    renderPage();
    await pageReady();

    const accounts = within(await screen.findByRole('table', { name: 'Service accounts' }));

    expect(accounts.getByText('A-000001')).toBeInTheDocument();
    expect(accounts.getByText('A-000002')).toBeInTheDocument();
    expect(accounts.getByText('12 Songsong Village Road, Songsong, Rota, MP 96951')).toBeInTheDocument();
    expect(accounts.getByText('8 Sinapalo Road, Sinapalo, Rota, MP 96951')).toBeInTheDocument();

    // The meter reaches the right row through the PREMISE; the premise with none shows a dash.
    expect(accounts.getByText('MTR-000001')).toBeInTheDocument();
    expect(accounts.getAllByText('—').length).toBeGreaterThan(0);
  });

  /** Cross-module rows are reached through the owning service — never a join, never a table read. */
  it('asks the accounts service for this customer rather than listing everything', async () => {
    renderPage();
    await screen.findByRole('table', { name: 'Service accounts' });

    expect(stub.lastCall('/api/service-accounts')?.searchParams.get('customerId')).toBe(record.id);
  });

  /** Each premise is fetched by id, so a capped list page can never silently drop one. */
  it('fetches each premise by id, once per distinct premise', async () => {
    renderPage();
    await screen.findByText('8 Sinapalo Road, Sinapalo, Rota, MP 96951');

    const premiseCalls = stub.calls.filter((url) => /^\/api\/service-locations\/[^/]+$/.test(url.pathname));
    expect(new Set(premiseCalls.map((url) => url.pathname)).size).toBe(2);
  });

  it('puts the open account above the closed one before a column is chosen', async () => {
    renderPage();

    const accounts = within(await screen.findByRole('table', { name: 'Service accounts' }));
    const numbers = accounts.getAllByText(/^A-0000\d\d$/).map((node) => node.textContent);

    expect(numbers).toEqual(['A-000001', 'A-000002']);
  });

  it('asks the meter register by premise, never by account', async () => {
    renderPage();
    await screen.findByText('MTR-000001');

    const meterCalls = stub.calls.filter((url) => url.pathname === '/api/meters');

    expect(meterCalls.length).toBeGreaterThan(0);
    expect(meterCalls.map((url) => url.searchParams.get('serviceLocationId')).toSorted()).toEqual(
      [otherPremise.id, premise.id].toSorted(),
    );

    // A meter has no account of its own, so asking for one would be asking the host a question it
    // cannot answer — and would quietly return every meter in the register.
    expect(meterCalls.every((url) => !url.searchParams.has('serviceAccountId'))).toBe(true);
    expect(meterCalls.every((url) => !url.searchParams.has('customerId'))).toBe(true);
  });

  /** The rest of an account is a drawer, so opening one does not lose the list behind it. */
  it('opens an account in a drawer, with its premise, meter and history', async () => {
    renderPage();

    const accounts = await screen.findByRole('table', { name: 'Service accounts' });
    await userEvent.click(within(accounts).getByRole('button', { name: /Songsong Village Road/ }));

    const drawer = within(await screen.findByRole('dialog'));

    expect(drawer.getByText('A-000001')).toBeInTheDocument();
    expect(drawer.getByText('Single phase')).toBeInTheDocument();
    expect(drawer.getByText('14,820.5')).toBeInTheDocument();

    // The transitions the machine would still allow, and the account's own service record.
    expect(drawer.getByText('Disconnected')).toBeInTheDocument();
    expect(drawer.getByText('Pending → Active')).toBeInTheDocument();
    expect(drawer.getByText('Account opened')).toBeInTheDocument();

    // The table is still behind it.
    expect(screen.getByRole('table', { name: 'Service accounts' })).toBeInTheDocument();
  });

  /**
   * The gap WP-2.10 closed. A list row carries no history — `ServiceAccountService.ListAsync`
   * includes none — so the old card's history section read an always-empty array in the running
   * app, however convincingly a hand-built fixture filled it in. It is its own request now.
   */
  it('fetches each account history from the history endpoint, not off the list row', async () => {
    renderPage();
    await screen.findByRole('table', { name: 'Service accounts' });

    const historyCalls = stub.calls.filter((url) => url.pathname.endsWith('/history'));

    expect(historyCalls.map((url) => url.pathname).toSorted()).toEqual(
      [`/api/service-accounts/${closed.id}/history`, `/api/service-accounts/${open.id}/history`].toSorted(),
    );
  });

  /** Billing and Payments are asked the same narrow question, and given a window rather than the lot. */
  it('asks Billing and Payments for this customer only, and for a bounded window', async () => {
    renderPage();
    await pageReady();

    const bills = stub.lastCall('/api/bills');
    const payments = stub.lastCall('/api/payments');

    expect(bills?.searchParams.get('customerId')).toBe(record.id);
    expect(payments?.searchParams.get('customerId')).toBe(record.id);
    expect(Number(bills?.searchParams.get('limit'))).toBeGreaterThan(0);
    expect(Number(payments?.searchParams.get('limit'))).toBeGreaterThan(0);

    // The corrections are what the timeline needs and what a list row does not carry by default.
    expect(bills?.searchParams.get('includeAdjustments')).toBe('true');
  });

  /**
   * Every query lives at the page, so a tab is a render rather than a round trip. Switching to
   * Bills must not re-ask Billing for what the summary already had.
   */
  it('does not re-fetch when a tab is opened', async () => {
    renderPage();
    await pageReady();

    const before = stub.calls.filter((url) => url.pathname === '/api/bills').length;

    await openTab('Bills');
    await screen.findByRole('table', { name: 'Bills' });

    expect(stub.calls.filter((url) => url.pathname === '/api/bills').length).toBe(before);
  });

  it('lists the bills as a sortable table on its own tab', async () => {
    renderPage();
    await openTab('Bills');

    const table = await screen.findByRole('table', { name: 'Bills' });
    const bills = within(table);

    expect(bills.getByText('BIL-000001')).toBeInTheDocument();
    expect(statusPills(table)).toEqual(['Issued']);
    expect(bills.getAllByText('$63.62').length).toBeGreaterThan(0);

    // The bill and the account are columns of their own — one identifier per cell, never stacked.
    expect(bills.getByRole('button', { name: /Bill/ })).toBeInTheDocument();
    expect(bills.getByText('A-000001')).toBeInTheDocument();

    // Sortable columns are what makes it a listing rather than a preview.
    expect(bills.getByRole('button', { name: /Outstanding/ })).toBeInTheDocument();
  });

  it('lists the payments as a sortable table on its own tab', async () => {
    renderPage();
    await openTab('Payments');

    const table = await screen.findByRole('table', { name: 'Payments' });
    const payments = within(table);

    expect(payments.getByText('PAY-000001')).toBeInTheDocument();
    expect(statusPills(table)).toEqual(['Approved']);
    expect(payments.getByText('Card')).toBeInTheDocument();
    expect(payments.getByRole('button', { name: /Amount/ })).toBeInTheDocument();

    // The payment and the bill it settled are columns of their own, not one stacked cell.
    expect(payments.getByText('BIL-000001')).toBeInTheDocument();
  });

  /** The merge this work package is about: four modules in one table, newest first. */
  it('merges accounts, bills and payments into one timeline table', async () => {
    renderPage();
    await openTab('Timeline');

    const feed = within(await screen.findByRole('table', { name: 'Account timeline' }));
    const rows = feed.getAllByRole('row').map((row) => row.textContent);

    expect(rows.some((text) => text?.includes('Payment taken'))).toBe(true);
    expect(rows.some((text) => text?.includes('Bill BIL-000001 issued'))).toBe(true);
    expect(rows.some((text) => text?.includes('Account A-000001 opened'))).toBe(true);

    // Newest first, across sources: the payment settled after the bill was issued, which was
    // months after the account was opened.
    const paid = rows.findIndex((text) => text?.includes('Payment taken'));
    const billed = rows.findIndex((text) => text?.includes('Bill BIL-000001 issued'));
    const opened = rows.findIndex((text) => text?.includes('Account A-000001 opened'));

    expect(paid).toBeLessThan(billed);
    expect(billed).toBeLessThan(opened);

    // The column that makes the merge legible: which module each row came from.
    expect(feed.getByText('Billing')).toBeInTheDocument();
    expect(feed.getByText('Payment')).toBeInTheDocument();
  });

  /**
   * The owner's call: the panel is here, it says why it is empty, and it asks nobody. There is no
   * work-order register to ask — `Modules.WorkOrders` is a bare `IModule` until Phase 3 — and a
   * request against a route that does not exist would render a 404 as a failure, which reads as
   * something broken rather than as something not built.
   */
  it('shows the work-orders tab as not built, and requests nothing for it', async () => {
    renderPage();
    await openTab('Work orders');

    expect(await screen.findByText('Work orders are not built yet')).toBeInTheDocument();
    expect(stub.calls.every((url) => !url.pathname.includes('work-order'))).toBe(true);
  });

  /** A tab is a route, so the link a rep pastes lands on the tab they were looking at. */
  it('opens straight onto a tab named in the URL', async () => {
    renderPage(fullWorld, `/customers/${record.id}/payments`);

    expect(await screen.findByRole('table', { name: 'Payments' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Customer record' })).not.toBeInTheDocument();
  });

  /** Failure path: a segment that names no tab is a typo, and the page returns to the customer. */
  it('sends an unrecognised tab segment back to the summary', async () => {
    renderPage(fullWorld, `/customers/${record.id}/bils`);

    expect(await screen.findByRole('heading', { name: 'Customer record' })).toBeInTheDocument();
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

  /**
   * WORK_PACKAGES.md's third check: a customer with no bills, no meters and no work orders renders
   * empty states rather than throwing. A prospect nobody has connected is an ordinary state.
   */
  it('renders every tab as an empty state for a customer with nothing behind them', async () => {
    renderPage(bareWorld);
    await pageReady();

    expect(await screen.findByText('No service accounts yet')).toBeInTheDocument();

    // The summary row is five honest zeros, not a blank strip.
    expect(screen.getByText('Nothing owed')).toBeInTheDocument();
    expect(screen.getByText('None settled yet')).toBeInTheDocument();

    await openTab('Bills');
    expect(await screen.findByText('No bills yet')).toBeInTheDocument();

    await openTab('Payments');
    expect(await screen.findByText('No payments yet')).toBeInTheDocument();

    await openTab('Timeline');
    expect(await screen.findByText('Nothing has happened yet')).toBeInTheDocument();

    await openTab('Work orders');
    expect(await screen.findByText('Work orders are not built yet')).toBeInTheDocument();
  });

  /** Failure path: the meter register can refuse on its own, and the accounts must still render. */
  it('keeps the accounts table when the meter register is unavailable', async () => {
    renderPage((url) =>
      url.pathname === '/api/meters'
        ? { status: 403, body: { title: 'Forbidden', status: 403, detail: 'metering.read required.' } }
        : fullWorld(url),
    );

    const accounts = within(await screen.findByRole('table', { name: 'Service accounts' }));

    expect(accounts.getByText('A-000001')).toBeInTheDocument();
    expect(accounts.queryByText('MTR-000001')).not.toBeInTheDocument();
  });

  /**
   * Failure path, and the point of every query owning its own state: Payments refusing leaves the
   * summary, the accounts and the bills tab exactly where they were.
   */
  it('keeps the other tabs when the payments register refuses', async () => {
    renderPage((url) =>
      url.pathname === '/api/payments'
        ? { status: 403, body: { title: 'Forbidden', status: 403, detail: 'payments.read required.' } }
        : fullWorld(url),
    );

    expect(await screen.findByRole('table', { name: 'Service accounts' })).toBeInTheDocument();

    await openTab('Bills');
    expect(await screen.findByRole('table', { name: 'Bills' })).toBeInTheDocument();

    await openTab('Payments');
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('You do not have access to this');
    expect(screen.getByText('payments.read required.')).toBeInTheDocument();
  });

  /**
   * Failure path: the feed is only as complete as its sources. A timeline quietly missing every
   * bill is not a shorter timeline, it is a wrong one, and a rep cannot tell by looking.
   */
  it('reports the timeline as failed when one of its sources did', async () => {
    renderPage((url) =>
      url.pathname === '/api/bills'
        ? { status: 500, body: { title: 'Server error', status: 500 } }
        : fullWorld(url),
    );

    await openTab('Timeline');

    expect(await screen.findByRole('alert')).toHaveTextContent('That did not load');
    expect(screen.queryByRole('table', { name: 'Account timeline' })).not.toBeInTheDocument();
  });

  /** The accounts query can fail on its own; the customer record above it must survive that. */
  it('keeps the customer record when the accounts query fails', async () => {
    renderPage((url) =>
      url.pathname === '/api/service-accounts'
        ? { status: 500, body: { title: 'Server error', status: 500 } }
        : fullWorld(url),
    );

    await pageReady();

    expect(screen.getByRole('heading', { name: 'Customer record' })).toBeInTheDocument();
    expect(await screen.findByRole('alert')).toHaveTextContent('That did not load');
  });
});
