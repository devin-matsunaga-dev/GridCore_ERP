import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router';
import { afterEach, describe, expect, it } from 'vitest';
import { CustomerDetailPage } from './customer-detail-page';
import { stubFetch, type FetchStub, type StubbedResponse } from '@/test/api-stub';
import { accountTransition, customer, serviceAccount, serviceLocation } from '@/test/registry-fixtures';
import { renderWithProviders } from '@/test/render';

/**
 * The transitions tab (WP-2.15): the two changes that alter what a customer is billed, and the
 * register of every one that has been made.
 *
 * Its own file, the split the contacts, deposit, notes and documents tabs already made. Everything
 * drives the real API client through a stubbed `fetch`, so the URL and the body each act produced
 * are part of what is asserted — a transition that stopped reaching the host would otherwise still
 * look right on screen, because the table re-renders from the refetch either way.
 */

const record = customer({ class: 'Residential', allowedTransitions: ['Suspended', 'Closed'] });

const transitionsPath = `/api/customers/${record.id}/transitions`;

const open = serviceAccount({ id: 'acct-open', accountNumber: 'A-000001', status: 'Active' });
const here = serviceLocation({ id: 'loc-here', locationCode: 'L-000001' });
const elsewhere = serviceLocation({
  id: 'loc-there',
  locationCode: 'L-000002',
  address: { ...serviceLocation().address, line1: '9 As Nieves Road' },
});

let stub: FetchStub;

afterEach(() => stub?.restore());

/** The 360 with its transitions tab answered, and every other panel empty. */
function world(overrides: (url: URL) => StubbedResponse | undefined = () => undefined) {
  return (url: URL): StubbedResponse | undefined => {
    const override = overrides(url);
    if (override) return override;

    if (url.pathname === `/api/customers/${record.id}`) return { body: record };
    if (url.pathname === transitionsPath) return { body: [] };
    if (url.pathname === '/api/service-accounts') return { body: [open] };
    if (url.pathname === '/api/service-locations') return { body: [here, elsewhere] };
    if (url.pathname === '/api/bills') return { body: [] };
    if (url.pathname === `/api/customers/${record.id}/contacts`) return { body: [] };
    if (url.pathname === `/api/customers/${record.id}/profile`) return { body: null };
    if (url.pathname === `/api/customers/${record.id}/deposits`) return { body: undefined };
    if (url.pathname === `/api/customers/${record.id}/notes`) return { body: [] };
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
    { route: `/customers/${record.id}/transitions` },
  );
}

/**
 * The body of the last POST the client sent to `path`.
 *
 * The URL alone is not the assertion here: a transition carries the reason code and the effective
 * date in its body, and a form that dropped either would still hit exactly the right route.
 */
function bodyOf(path: string): Record<string, unknown> {
  expect(stub.lastCall(path)).toBeDefined();

  return stub.lastBody(path) as Record<string, unknown>;
}

describe('the transitions tab', () => {
  it('is a route, so the tab a rep pastes is the tab that opens', async () => {
    renderTab();

    expect(await screen.findByRole('heading', { name: 'Account transitions' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Customer record' })).not.toBeInTheDocument();
  });

  it('says what the customer is billed as and since when', async () => {
    renderTab(
      world((url) =>
        url.pathname === `/api/customers/${record.id}`
          ? { body: { ...record, classEffectiveOn: '2026-09-01' } }
          : undefined,
      ),
    );

    // Scoped to the block the label heads: the page header also names the class, and a bare
    // getByText would be asserting that the subtitle rendered rather than that this tab did.
    const label = await screen.findByText('Billed as');

    expect(within(label.closest('div')!).getByText('Residential')).toBeInTheDocument();
    expect(screen.getByText(/Commercial or residential since/)).toBeInTheDocument();
  });

  it('reads a null effective date as "since registration" rather than as unknown', async () => {
    // The ordinary case: a customer who has never been re-classified is on the class they were
    // opened under, from the day they were opened. "Unknown" would be the screen inventing a gap.
    renderTab();

    expect(await screen.findByText(/On the class this customer was registered under/)).toBeInTheDocument();
  });

  it('records a class change with a reason code and an effective date', async () => {
    renderTab();

    await userEvent.click(await screen.findByRole('button', { name: /Change to commercial/ }));

    const reason = screen.getByLabelText('Reason');
    await userEvent.selectOptions(reason, 'PremiseNowTrading');

    await userEvent.clear(screen.getByLabelText('Effective from'));
    await userEvent.type(screen.getByLabelText('Effective from'), '2026-09-01');

    await userEvent.click(screen.getByRole('button', { name: 'Change class' }));

    const body = bodyOf(`${transitionsPath}/class`);

    // The class is stated by the screen rather than picked, because there are two of them and the
    // host refuses a move to the one already held.
    expect(body.class).toBe('Commercial');
    expect(body.reasonCode).toBe('PremiseNowTrading');
    expect(body.effectiveOn).toBe('2026-09-01');
  });

  it('offers only the statuses WP-1.2s machine allows from where the customer stands', async () => {
    // Offering the rest would be offering 409s — DESIGN.md's rule for every state machine in the
    // product.
    renderTab();

    await userEvent.click(await screen.findByRole('button', { name: 'Change status' }));

    const options = within(screen.getByLabelText('New status')).getAllByRole('option');

    expect(options.map((option) => option.textContent)).toEqual(['Suspended', 'Closed']);
  });

  it('refuses to send the escape hatch without a sentence', async () => {
    // Failure path, and the rule the fixed list depends on: a list whose escape hatch may be silent
    // is a fixed list in name only. Caught here so the rep is told before they press save rather
    // than by a 400 afterwards.
    renderTab();

    await userEvent.click(await screen.findByRole('button', { name: /Change to commercial/ }));
    await userEvent.selectOptions(screen.getByLabelText('Reason'), 'Other');
    await userEvent.click(screen.getByRole('button', { name: 'Change class' }));

    expect(await screen.findByText(/A fixed list is only fixed if its escape hatch explains itself/)).toBeInTheDocument();
    expect(stub.calls.some((url) => url.pathname === `${transitionsPath}/class`)).toBe(false);
  });

  it('sends a transfer naming the account being left and the premise being taken up', async () => {
    renderTab();

    await userEvent.click(await screen.findByRole('button', { name: 'Transfer' }));
    await userEvent.selectOptions(screen.getByLabelText('Premise to move to'), elsewhere.id);
    await userEvent.click(screen.getByRole('button', { name: 'Transfer' }));

    const body = bodyOf(`${transitionsPath}/transfer`);

    expect(body.fromServiceAccountId).toBe(open.id);
    expect(body.toServiceLocationId).toBe(elsewhere.id);
    expect(body.reasonCode).toBe('Relocation');
  });

  it('disables moving out and transferring when nothing is open to move', async () => {
    // A button that 409s on click is a button that made the rep find out the hard way.
    renderTab(
      world((url) =>
        url.pathname === '/api/service-accounts'
          ? { body: [serviceAccount({ status: 'Closed' })] }
          : undefined,
      ),
    );

    expect(await screen.findByRole('button', { name: 'Move out' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Transfer' })).toBeDisabled();

    // Moving IN is still offered: it needs a premise, not an open account.
    expect(screen.getByRole('button', { name: 'Move in' })).toBeEnabled();
  });

  it('marks a transition whose effective date is not the day it was recorded', async () => {
    // The mark a back-dated re-classification would otherwise hide behind.
    renderTab(
      world((url) =>
        url.pathname === transitionsPath
          ? {
              body: [
                accountTransition({
                  id: 'dated',
                  recordedAt: '2026-08-26T10:15:00+00:00',
                  effectiveOn: '2026-07-01',
                }),
              ],
            }
          : undefined,
      ),
    );

    const table = within(await screen.findByRole('table', { name: 'Account transitions' }));

    expect(table.getByText(/· dated/)).toBeInTheDocument();
    expect(table.getByText('Residential → Commercial')).toBeInTheDocument();
  });

  it('shows a carried deposit as a figure and everything else as nothing at all', async () => {
    // A zero in a money column reads as a figure somebody worked out. Nothing was carried on a class
    // change, so the column says so with an em dash.
    renderTab(
      world((url) =>
        url.pathname === transitionsPath
          ? {
              body: [
                accountTransition({ id: 'carried', kind: 'Transferred', fromValue: 'A-000001', toValue: 'A-000002', depositCarried: 250 }),
                accountTransition({ id: 'class' }),
              ],
            }
          : undefined,
      ),
    );

    const table = within(await screen.findByRole('table', { name: 'Account transitions' }));

    expect(table.getByText('$250.00')).toBeInTheDocument();
    expect(table.getAllByText('—').length).toBeGreaterThan(0);
  });

  it('renders an empty register as an empty state rather than as a failure', async () => {
    renderTab();

    expect(await screen.findByText('No transitions recorded')).toBeInTheDocument();
  });
});
