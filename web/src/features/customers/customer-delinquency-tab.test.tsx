import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router';
import { afterEach, describe, expect, it } from 'vitest';
import { CustomerDetailPage } from './customer-detail-page';
import { stubFetch, type FetchStub, type StubbedResponse } from '@/test/api-stub';
import {
  customer,
  delinquency,
  disconnectionEligibility,
  dunningNotice,
  serviceAccount,
  serviceLocation,
} from '@/test/registry-fixtures';
import { renderWithProviders } from '@/test/render';
import { formatDate } from '@/lib/format';

/**
 * The delinquency tab (WP-2.19).
 *
 * Its own file, the split every other 360° tab already made. Everything drives the real API client
 * through a stubbed `fetch`, so the URL and the body each act produced are part of what is asserted —
 * and on this screen that matters more than most: one of its buttons spends the customer's deposit,
 * and a button that stopped reaching the host would still look right.
 */

const record = customer();
const account = serviceAccount({ id: 'acct-1', accountNumber: 'A-000001', status: 'Active' });
const premise = serviceLocation();

let stub: FetchStub;

afterEach(() => stub?.restore());

/** The 360 with its delinquency tab answered, and every other panel empty. */
function world(
  picture: unknown = delinquency({ serviceAccountId: account.id, accountNumber: account.accountNumber }),
) {
  return (url: URL): StubbedResponse | undefined => {
    if (url.pathname === `/api/customers/${record.id}`) return { body: record };
    if (url.pathname === '/api/service-accounts') return { body: [account] };
    if (url.pathname === '/api/service-locations') return { body: [premise] };
    if (url.pathname === `/api/service-accounts/${account.id}/delinquency`) return { body: picture };
    if (url.pathname === `/api/service-accounts/${account.id}/disconnection-eligibility`) {
      return {
        body: {
          eligibility: disconnectionEligibility({ offsetAmount: 200, arrearsAfterOffset: 0, isOffsetApplied: true, depositClearsArrears: true }),
          offsetAmount: 200,
          offsetEntries: [],
        },
      };
    }
    if (url.pathname === `/api/service-accounts/${account.id}/dunning-notices`) return { body: dunningNotice() };
    if (url.pathname === '/api/bills') return { body: [] };
    if (url.pathname === '/api/account-charges') return { body: [] };
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
    { route: `/customers/${record.id}/delinquency` },
  );
}

describe('the delinquency tab', () => {
  it('is a route, so the tab a rep pastes is the tab that opens', async () => {
    renderTab();

    expect(await screen.findByRole('heading', { name: 'Delinquency' })).toBeInTheDocument();
  });

  it('shows what is past due beside what is merely owed', async () => {
    // The distinction the 1% late charge and the disconnection threshold both turn on: a bill issued
    // last week and due next month is not money the customer is late with.
    renderTab();

    await screen.findByRole('heading', { name: 'Delinquency' });

    // Scoped to each figure's own card, because "Not yet due" is both a figure and an ageing band —
    // which is the point: the same money is reported twice, once against what is late and once
    // against how old it is.
    const pastDue = screen.getByText('Past due').closest('div')!;
    const notYetDue = screen.getByText('Not yet due', { selector: 'p' }).closest('div')!;

    expect(within(pastDue).getByText('$200.00')).toBeInTheDocument();
    expect(within(notYetDue).getByText('$80.00')).toBeInTheDocument();
    expect(screen.getByText(/Oldest 90 days late/)).toBeInTheDocument();
  });

  it('ages the debt into the bands the host published, and totals to what is outstanding', async () => {
    renderTab();

    await screen.findByRole('heading', { name: 'Delinquency' });

    expect(screen.getByText('61-90 days')).toBeInTheDocument();
    expect(screen.getByText('Total outstanding')).toBeInTheDocument();
    expect(screen.getByText('$280.00')).toBeInTheDocument();

    // The empty bands are not rendered: five zeroes say less than one figure.
    expect(screen.queryByText('1-30 days')).not.toBeInTheDocument();
  });

  it('reads the four tests off the host, with the figures behind each answer', async () => {
    // No rule is re-implemented in the browser. Whether this customer may be cut off is decided by
    // the host, and a second opinion here is the last thing this screen should hold.
    renderTab();

    await screen.findByRole('heading', { name: 'Disconnection eligibility' });

    expect(screen.getByText('Arrears at or over the published threshold')).toBeInTheDocument();
    expect(screen.getByText('Disconnection notice served')).toBeInTheDocument();
    expect(screen.getByText('Statutory waiting period elapsed')).toBeInTheDocument();
    expect(screen.getByText('No payment arrangement in force')).toBeInTheDocument();
    expect(screen.getByText('No disconnection notice has been served on this account.')).toBeInTheDocument();

    expect(screen.getByText(/2 of the four tests are outstanding/)).toBeInTheDocument();
  });

  it('says the deposit clears the arrears when it does, and that the account is therefore safe', async () => {
    // THE STATUTE, on screen. This is the sentence a rep reads out to somebody who rang up expecting
    // to be cut off.
    renderTab(
      world(
        delinquency({
          serviceAccountId: account.id,
          depositHeld: 300,
          eligibility: disconnectionEligibility({
            depositHeldBeforeOffset: 300,
            offsetAmount: 200,
            arrearsAfterOffset: 0,
            depositClearsArrears: true,
          }),
        }),
      ),
    );

    expect(
      await screen.findByText(/security deposit clears the arrears, so this account is not eligible/),
    ).toBeInTheDocument();
  });

  it('spells out what evaluating will do before the button that does it', async () => {
    renderTab(
      world(
        delinquency({
          serviceAccountId: account.id,
          depositHeld: 300,
          eligibility: disconnectionEligibility({ depositHeldBeforeOffset: 300, offsetAmount: 200 }),
        }),
      ),
    );

    expect(await screen.findByText(/CNMI Public Law 16-17/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Apply deposit and evaluate' })).toBeInTheDocument();
  });

  it('evaluates through the POST that moves the money, never a GET', async () => {
    // A GET that applied a deposit would apply it again on every refresh — which is why the read
    // and the evaluation are two different routes.
    renderTab(
      world(
        delinquency({
          serviceAccountId: account.id,
          depositHeld: 300,
          eligibility: disconnectionEligibility({ depositHeldBeforeOffset: 300, offsetAmount: 200 }),
        }),
      ),
    );

    await userEvent.click(await screen.findByRole('button', { name: 'Apply deposit and evaluate' }));

    // A body is what proves it was the POST rather than a read: the GET beside it carries none.
    expect(stub.lastSentBody(`/api/service-accounts/${account.id}/disconnection-eligibility`)).toEqual({
      asOf: null,
    });
  });

  it('offers to record the notice the sequence has reached, and says what serving it starts', async () => {
    renderTab();

    await screen.findByRole('heading', { name: 'Dunning notices' });

    expect(screen.getByText('Notice of disconnection is due')).toBeInTheDocument();
    expect(screen.getByText(/10 days to wait after service/)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Record as served' }));

    // By its code, and with no date: the host stamps the day it went out, exactly as it prices a fee.
    expect(stub.lastSentBody(`/api/service-accounts/${account.id}/dunning-notices`)).toEqual({
      noticeType: 'Disconnection',
    });
  });

  it('offers no notice once the step it has reached has been served', async () => {
    renderTab(
      world(
        delinquency({
          serviceAccountId: account.id,
          notices: [dunningNotice({ noticeType: 'Disconnection' })],
        }),
      ),
    );

    await screen.findByRole('heading', { name: 'Dunning notices' });

    expect(screen.queryByRole('button', { name: 'Record as served' })).not.toBeInTheDocument();
  });

  it('shows a served notice with the day it went out and the day it takes effect', async () => {
    // The record IS the evidence that the customer was warned, so both dates are on the row.
    renderTab(
      world(
        delinquency({
          serviceAccountId: account.id,
          notices: [dunningNotice({ noticeType: 'Disconnection', servedOn: '2026-08-10', effectiveFrom: '2026-08-20' })],
        }),
      ),
    );

    await screen.findByRole('heading', { name: 'Dunning notices' });

    // Through the shared formatter rather than a literal, so this asserts what a rep reads whatever
    // locale the browser is in.
    expect(screen.getByText(`Served ${formatDate('2026-08-10')}`)).toBeInTheDocument();
    expect(screen.getByText(new RegExp(`effective from ${formatDate('2026-08-20')}`))).toBeInTheDocument();
  });

  it('asks for nothing until it knows which supply it is about', async () => {
    // Delinquency is per SERVICE ACCOUNT, not per customer: a house may take an electric, a water
    // and a wastewater account, and each is delinquent on its own.
    renderTab();

    await screen.findByRole('heading', { name: 'Delinquency' });

    expect(screen.getByLabelText('Account')).toHaveValue(account.id);
    expect(stub.lastCall(`/api/service-accounts/${account.id}/delinquency`)).toBeDefined();
  });
});
