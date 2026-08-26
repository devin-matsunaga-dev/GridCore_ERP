import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router';
import { afterEach, describe, expect, it } from 'vitest';
import { CustomerDetailPage } from './customer-detail-page';
import { stubFetch, type FetchStub, type StubbedResponse } from '@/test/api-stub';
import { accountCharge, customer, feeScheduleEntry, serviceAccount, serviceLocation } from '@/test/registry-fixtures';
import { renderWithProviders } from '@/test/render';

/**
 * The charges tab — WP-2.16's screen, shipped with WP-2.18.
 *
 * Its own file, the split every other 360° tab already made. Everything drives the real API client
 * through a stubbed `fetch`, so the URL and the body each act produced are part of what is asserted:
 * a fee that stopped reaching the host would otherwise still look right on screen, because the table
 * re-renders from the refetch either way.
 */

const record = customer();
const account = serviceAccount({ id: 'acct-1', accountNumber: 'A-000001', status: 'Active' });
const premise = serviceLocation();

const pending = accountCharge({ serviceAccountId: account.id, customerId: record.id });

const schedule = [
  feeScheduleEntry({ code: 'ServiceConnection', amount: 135 }),
  feeScheduleEntry({ code: 'Reconnection', amount: 60, feeScheduleId: 'fee-2', effectiveFrom: '2026-07-01' }),
];

let stub: FetchStub;

afterEach(() => stub?.restore());

/** The 360 with its charges tab answered, and every other panel empty. */
function world(
  charges: unknown[] = [pending],
  overrides: (url: URL) => StubbedResponse | undefined = () => undefined,
) {
  return (url: URL): StubbedResponse | undefined => {
    const override = overrides(url);
    if (override) return override;

    if (url.pathname === `/api/customers/${record.id}`) return { body: record };
    if (url.pathname === '/api/service-accounts') return { body: [account] };
    if (url.pathname === '/api/service-locations') return { body: [premise] };
    if (url.pathname === '/api/account-charges') return { body: charges };
    if (url.pathname === '/api/fee-schedule') return { body: schedule };
    if (url.pathname === '/api/bills') return { body: [] };
    if (url.pathname === `/api/customers/${record.id}/contacts`) return { body: [] };
    if (url.pathname === `/api/customers/${record.id}/profile`) return { body: null };
    if (url.pathname === `/api/customers/${record.id}/deposits`) return { body: undefined };
    if (url.pathname === `/api/customers/${record.id}/notes`) return { body: [] };
    if (url.pathname === `/api/customers/${record.id}/transitions`) return { body: [] };
    if (url.pathname === '/api/payments') return { body: [] };

    return undefined;
  };
}

function renderTab(respond: (url: URL) => StubbedResponse | undefined = world()) {
  stub = stubFetch(respond);

  return renderWithProviders(
    <Routes>
      <Route path="/customers/:customerId" element={<CustomerDetailPage />} />
      <Route path="/customers/:customerId/:tab" element={<CustomerDetailPage />} />
    </Routes>,
    { route: `/customers/${record.id}/charges` },
  );
}

describe('the charges tab', () => {
  it('is a route, so the tab a rep pastes is the tab that opens', async () => {
    renderTab();

    expect(await screen.findByRole('heading', { name: 'Charges' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Customer record' })).not.toBeInTheDocument();
  });

  it('reads the register for this customer and the catalogue for today', async () => {
    renderTab();

    await screen.findByRole('heading', { name: 'Charges' });

    expect(stub.lastCall('/api/account-charges')?.searchParams.get('customerId')).toBe(record.id);
    expect(stub.lastCall('/api/fee-schedule')).toBeDefined();
  });

  it('leads with what has been raised and not yet billed, and says it is not a balance', async () => {
    renderTab(
      world([
        accountCharge({ id: 'a', amount: 135, serviceAccountId: account.id }),
        accountCharge({ id: 'b', amount: 60, status: 'Billed', isPending: false, serviceAccountId: account.id }),
      ]),
    );

    const label = await screen.findByText('Raised and not yet billed');

    expect(within(label.closest('div')!).getByText('$135.00')).toBeInTheDocument();
    expect(screen.getByText(/It is not a balance until it does/)).toBeInTheDocument();
  });

  it('prices the chosen fee off the published schedule and offers no amount field', async () => {
    renderTab();

    await screen.findByRole('heading', { name: 'Charges' });

    // The catalogue's figure, quoted before anything is raised — and there is deliberately no field
    // a rep could type a different one into.
    expect(screen.getByText(/\$135\.00 USD — published from/)).toBeInTheDocument();

    await userEvent.selectOptions(screen.getByLabelText('Fee'), 'Reconnection');

    expect(screen.getByText(/\$60\.00 USD — published from/)).toBeInTheDocument();
    expect(screen.queryByLabelText('Amount')).not.toBeInTheDocument();
  });

  it('will not raise a fee without an account and a reason', async () => {
    renderTab();

    const raise = await screen.findByRole('button', { name: 'Raise fee' });

    expect(raise).toBeDisabled();

    await userEvent.selectOptions(screen.getByLabelText('Account'), account.id);
    expect(raise).toBeDisabled();

    await userEvent.type(screen.getByLabelText('Reason'), 'New connection approved.');
    expect(raise).toBeEnabled();
  });

  it('raises the fee by its code, never by its amount', async () => {
    renderTab();

    await userEvent.selectOptions(await screen.findByLabelText('Account'), account.id);
    await userEvent.selectOptions(screen.getByLabelText('Fee'), 'Reconnection');
    await userEvent.type(screen.getByLabelText('Reason'), 'Supply restored after payment.');
    await userEvent.click(screen.getByRole('button', { name: 'Raise fee' }));

    // lastSentBody, not lastBody: raising a fee is followed by a refetch of the same path, and
    // the GET's absent body would otherwise hide the POST this test is about.
    const body = stub.lastSentBody('/api/account-charges') as Record<string, unknown>;

    expect(body).toMatchObject({
      serviceAccountId: account.id,
      code: 'Reconnection',
      reason: 'Supply restored after payment.',
    });

    // The host prices it. A browser that sent an amount would be a browser inventing a published fee.
    expect(body).not.toHaveProperty('amount');
  });

  it('offers a counter bill and a withdrawal on a pending charge, and neither on a billed one', async () => {
    renderTab(
      world([
        pending,
        accountCharge({ id: 'billed', status: 'Billed', isPending: false, billNumber: 'B-000009', serviceAccountId: account.id }),
      ]),
    );

    await screen.findByRole('heading', { name: 'Pending charges' });

    // One pending charge, so one pair of buttons — the billed one is terminal on the host.
    expect(screen.getAllByRole('button', { name: 'Bill at the counter' })).toHaveLength(1);
    expect(screen.getAllByRole('button', { name: 'Withdraw' })).toHaveLength(1);

    await userEvent.click(screen.getByRole('button', { name: 'Bill at the counter' }));

    expect(stub.lastCall(`/api/account-charges/${pending.id}/bill`)).toBeDefined();
  });

  it('withdraws a pending charge with a reason, because it removes money the utility was owed', async () => {
    renderTab();

    await userEvent.click(await screen.findByRole('button', { name: 'Withdraw' }));

    expect(stub.lastBody(`/api/account-charges/${pending.id}/cancel`)).toMatchObject({
      reason: expect.any(String),
    });
  });

  it('says what to do first when the customer holds no account to charge', async () => {
    renderTab(
      world([], (url) => (url.pathname === '/api/service-accounts' ? { body: [] } : undefined)),
    );

    expect(await screen.findByText(/holds none yet. Approve their application first/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Raise fee' })).not.toBeInTheDocument();
  });

  it('renders a refusal as an error rather than as an empty register', async () => {
    renderTab(
      world([], (url) =>
        url.pathname === '/api/account-charges'
          ? { status: 403, body: { title: 'Not permitted', status: 403, detail: 'You do not have permission to do that.' } }
          : undefined,
      ),
    );

    expect(await screen.findByText(/do not have permission/i)).toBeInTheDocument();
  });
});
