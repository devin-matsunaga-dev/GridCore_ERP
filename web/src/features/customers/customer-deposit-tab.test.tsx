import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router';
import { afterEach, describe, expect, it } from 'vitest';
import { CustomerDetailPage } from './customer-detail-page';
import { stubFetch, type FetchStub, type StubbedResponse } from '@/test/api-stub';
import { customer, depositEntry, depositLedger } from '@/test/registry-fixtures';
import { bill, paidBill } from '@/test/revenue-cycle-fixtures';
import { renderWithProviders } from '@/test/render';

/**
 * The deposit tab (WP-2.12): what the utility is holding, and the three ways it moves.
 *
 * Its own file rather than more of `customer-detail-page.test.tsx`, the split the contacts tab
 * already made. Everything drives the real API client through a stubbed `fetch`, so the URL and the
 * body each action produced are part of what is asserted — a movement that stopped reaching the
 * host would otherwise still look right on screen, because the table re-renders from the refetch
 * either way.
 */

const record = customer();
const depositsPath = `/api/customers/${record.id}/deposits`;

let stub: FetchStub;

afterEach(() => stub?.restore());

const owedBill = bill({ balance: 63.62, amountPaid: 0 });

/** The 360 with its deposit tab answered, and every other panel empty. */
function world(overrides: (url: URL) => StubbedResponse | undefined = () => undefined) {
  return (url: URL): StubbedResponse | undefined => {
    const override = overrides(url);
    if (override) return override;

    if (url.pathname === `/api/customers/${record.id}`) return { body: record };
    if (url.pathname === depositsPath) return { body: depositLedger() };
    if (url.pathname === '/api/bills') return { body: [owedBill] };
    if (url.pathname === `/api/customers/${record.id}/contacts`) return { body: [] };
    if (url.pathname === `/api/customers/${record.id}/profile`) return { body: null };
    if (url.pathname === '/api/service-accounts') return { body: [] };
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
    { route: `/customers/${record.id}/deposit` },
  );
}

async function movementsTable(respond: (url: URL) => StubbedResponse | undefined = world()) {
  renderTab(respond);

  return within(await screen.findByRole('table', { name: 'Deposit movements' }));
}

/** The body of the last POST the client sent to `path`. */
function bodyOf(path: string): unknown {
  const call = stub.calls.findLast((url) => url.pathname === path);

  expect(call).toBeDefined();

  return call;
}

describe('the deposit tab', () => {
  it('is a route, so the tab a rep pastes is the tab that opens', async () => {
    renderTab();

    expect(await screen.findByRole('table', { name: 'Deposit movements' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Customer record' })).not.toBeInTheDocument();
  });

  it('shows what is held beside what the schedule asks', async () => {
    renderTab();

    expect(await screen.findByText('Held on account')).toBeInTheDocument();

    // The schedule is what a rep quotes on the telephone, so it is on screen beside the balance
    // rather than a click away.
    expect(screen.getByText(/Schedule asks \$450\.00 for a commercial customer/)).toBeInTheDocument();
    expect(screen.getByText('Schedule met')).toBeInTheDocument();
  });

  it('says how far short a part-paid deposit is, and does not read as an error', async () => {
    // A deposit below the schedule is ordinary — a part-payment at the counter, or money spent on a
    // bill. It is a warning pill, not a danger one.
    renderTab(
      world((url) =>
        url.pathname === depositsPath
          ? { body: depositLedger({ balance: 200, shortfallAmount: 250, entries: [depositEntry({ amount: 200, balanceAfter: 200, signedAmount: 200 })] }) }
          : undefined,
      ),
    );

    expect(await screen.findByText(/\$250\.00 short/)).toBeInTheDocument();
    expect(screen.getByText('Below the schedule')).toBeInTheDocument();
  });

  it('lists the movements as a table with the running balance on each row', async () => {
    // A ledger that cannot show its own running balance is a ledger somebody has to re-add by hand
    // to check. The figure is the host's, stored on the entry rather than recomputed here.
    const table = await movementsTable(
      world((url) =>
        url.pathname === depositsPath
          ? {
              body: depositLedger({
                balance: 410,
                shortfallAmount: 40,
                entries: [
                  depositEntry({ id: 'entry-1', amount: 450, signedAmount: 450, balanceAfter: 450 }),
                  depositEntry({
                    id: 'entry-2',
                    kind: 'Applied',
                    amount: 40,
                    signedAmount: -40,
                    balanceAfter: 410,
                    billNumber: 'BIL-000001',
                    recordedAt: '2026-03-11T00:30:00+00:00',
                  }),
                ],
              }),
            }
          : undefined,
      ),
    );

    expect(table.getByText('Applied to bill')).toBeInTheDocument();
    expect(table.getByText('Collected')).toBeInTheDocument();

    // Signed in the column though never signed in the ledger: a rep scanning "where did the deposit
    // go" needs money out to read differently from money in.
    expect(table.getByText(/−\$40\.00/)).toBeInTheDocument();
    expect(table.getByText(/\+\$450\.00/)).toBeInTheDocument();

    // Its own column and not wrapped into the reason — the WP-2.10 rule for an identifier.
    expect(table.getByText('BIL-000001')).toBeInTheDocument();
  });

  it('offers no way to edit the balance', async () => {
    // WP-2.12's whole point. The balance is a projection of immutable entries, so there is no field
    // for it — a screen that let a rep type over it is the defect this package removed.
    renderTab();

    await screen.findByRole('table', { name: 'Deposit movements' });

    expect(screen.queryByRole('button', { name: /edit deposit/i })).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/deposit held/i)).not.toBeInTheDocument();
  });

  it('says so rather than showing an empty table when nothing has ever been taken', async () => {
    renderTab(
      world((url) =>
        url.pathname === depositsPath
          ? { body: depositLedger({ balance: 0, shortfallAmount: 450, entries: [] }) }
          : undefined,
      ),
    );

    expect(await screen.findByText('No deposit movements')).toBeInTheDocument();
    expect(screen.getByText('None held')).toBeInTheDocument();
  });

  it('will not offer to apply or refund a deposit that is not there', async () => {
    // A button that 409s on click is a button that made the rep find out the hard way.
    renderTab(
      world((url) =>
        url.pathname === depositsPath
          ? { body: depositLedger({ balance: 0, shortfallAmount: 450, entries: [] }) }
          : undefined,
      ),
    );

    await screen.findByText('No deposit movements');

    expect(screen.getByRole('button', { name: 'Apply to a bill' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Refund' })).toBeDisabled();

    // Collecting is still on, because that is how the balance stops being zero.
    expect(screen.getByRole('button', { name: 'Collect' })).toBeEnabled();
  });

  it('will not offer to apply a deposit when there is no outstanding bill', async () => {
    renderTab(world((url) => (url.pathname === '/api/bills' ? { body: [paidBill()] } : undefined)));

    await screen.findByRole('table', { name: 'Deposit movements' });

    expect(screen.getByRole('button', { name: 'Apply to a bill' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Refund' })).toBeEnabled();
  });

  it('collects a deposit through the host', async () => {
    const user = userEvent.setup();

    renderTab();

    await screen.findByRole('table', { name: 'Deposit movements' });

    await user.click(screen.getByRole('button', { name: 'Collect' }));
    await user.type(screen.getByLabelText('Amount'), '50.00');
    await user.click(screen.getByRole('button', { name: 'Collect', hidden: false }));

    expect(bodyOf(`${depositsPath}/collections`)).toBeDefined();
  });

  it('refuses a refund larger than the balance before it reaches the host', async () => {
    // The browser refuses what the host would refuse — WP-2.8's call, kept. The host stays the
    // authority; the duplication buys the rep the answer at the moment it becomes wrong.
    const user = userEvent.setup();

    renderTab();

    await screen.findByRole('table', { name: 'Deposit movements' });

    await user.click(screen.getByRole('button', { name: 'Refund' }));
    await user.type(screen.getByLabelText('Amount'), '451.00');
    await user.click(screen.getByRole('button', { name: 'Refund', hidden: false }));

    expect(await screen.findByText('Only $450.00 is held.')).toBeInTheDocument();
    expect(stub.calls.some((url) => url.pathname === `${depositsPath}/refunds`)).toBe(false);
  });

  it('refuses more than a bill has outstanding before it reaches the host', async () => {
    // The other ceiling. $450 is held and the bill is owed $63.62, so the bill is what runs out
    // first — and the hint says so before anything is typed.
    const user = userEvent.setup();

    renderTab();

    await screen.findByRole('table', { name: 'Deposit movements' });

    await user.click(screen.getByRole('button', { name: 'Apply to a bill' }));

    expect(screen.getByText(/Up to \$63\.62/)).toBeInTheDocument();

    await user.type(screen.getByLabelText('Amount'), '100.00');
    await user.click(screen.getByRole('button', { name: 'Apply', hidden: false }));

    expect(await screen.findByText('Only $63.62 can go against this bill.')).toBeInTheDocument();
    expect(stub.calls.some((url) => url.pathname === `${depositsPath}/applications`)).toBe(false);
  });

  it('refuses an amount finer than a cent', async () => {
    const user = userEvent.setup();

    renderTab();

    await screen.findByRole('table', { name: 'Deposit movements' });

    await user.click(screen.getByRole('button', { name: 'Collect' }));
    await user.type(screen.getByLabelText('Amount'), '10.005');
    await user.click(screen.getByRole('button', { name: 'Collect', hidden: false }));

    expect(await screen.findByText('Amounts are to the cent.')).toBeInTheDocument();
    expect(stub.calls.some((url) => url.pathname === `${depositsPath}/collections`)).toBe(false);
  });

  it('does NOT cap a collection at the schedule', async () => {
    // Deliberate, and the one ceiling that is absent. WP-2.8 refuses an intake that collects more
    // than the class is assessed; a later collection rebuilds a deposit spent on a bill, or asks
    // more of a customer with a run of arrears, and a cap here would refuse both.
    const user = userEvent.setup();

    renderTab();

    await screen.findByRole('table', { name: 'Deposit movements' });

    await user.click(screen.getByRole('button', { name: 'Collect' }));
    await user.type(screen.getByLabelText('Amount'), '1000.00');
    await user.click(screen.getByRole('button', { name: 'Collect', hidden: false }));

    expect(bodyOf(`${depositsPath}/collections`)).toBeDefined();
  });

  it('leaves the form open when the host refuses the movement', async () => {
    // The 403 the WP names, as a rep meets it: a caller without customers.deposit. The message is a
    // toast, which is not mounted in the fast tier — what IS asserted here is that the screen did
    // not pretend it worked. Nothing on this page is optimistic, so a refused refund leaves the
    // balance where it was and the form where the rep can correct it.
    const user = userEvent.setup();

    renderTab(
      world((url) =>
        url.pathname === `${depositsPath}/refunds`
          ? {
              status: 403,
              body: {
                title: 'Not permitted',
                status: 403,
                detail: "Moving a customer's security deposit requires the 'customers.deposit' permission.",
              },
            }
          : undefined,
      ),
    );

    await screen.findByRole('table', { name: 'Deposit movements' });

    await user.click(screen.getByRole('button', { name: 'Refund' }));
    await user.type(screen.getByLabelText('Amount'), '50.00');
    await user.click(screen.getByRole('button', { name: 'Refund', hidden: false }));

    expect(bodyOf(`${depositsPath}/refunds`)).toBeDefined();

    // Still open, still holding what it held.
    expect(screen.getByLabelText('Amount')).toHaveValue('50.00');
    expect(screen.getByText('Schedule met')).toBeInTheDocument();
  });

  it('issues no request when a rep switches to it', async () => {
    // WP-2.10's rule: every query lives at the page, so switching tabs fetches nothing.
    const user = userEvent.setup();

    renderTab();

    await screen.findByRole('table', { name: 'Deposit movements' });

    const before = stub.calls.length;

    await user.click(screen.getByRole('link', { name: 'Bills' }));
    await screen.findByRole('table', { name: 'Bills' });

    await user.click(screen.getByRole('link', { name: 'Deposit' }));
    await screen.findByRole('table', { name: 'Deposit movements' });

    expect(stub.calls.length).toBe(before);
  });
});
