import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router';
import { afterEach, describe, expect, it } from 'vitest';
import { CustomerDetailPage } from './customer-detail-page';
import { stubFetch, type FetchStub, type StubbedResponse } from '@/test/api-stub';
import {
  arrangementInstalments,
  arrangementLimits,
  customer,
  delinquency,
  paymentArrangement,
  serviceAccount,
  serviceLocation,
} from '@/test/registry-fixtures';
import { renderWithProviders } from '@/test/render';

/**
 * The arrangements tab (WP-2.20).
 *
 * Its own file, the split every other 360° tab already made. Everything drives the real API client
 * through a stubbed `fetch`, so the URL and the body each act produced are part of what is asserted —
 * and here that matters because one of these buttons is what stops a customer's supply being cut
 * off.
 */

const record = customer();
const account = serviceAccount({ id: 'acct-1', accountNumber: 'A-000001', status: 'Active' });
const premise = serviceLocation();

let stub: FetchStub;

afterEach(() => stub?.restore());

/**
 * An arrangement against THIS test's account.
 *
 * The shared fixture defaults to the fixture account, and the activation route is built from the
 * arrangement's own `serviceAccountId` — so a test using the bare fixture would post to a URL no
 * rep's browser ever would.
 */
function anArrangement(overrides: Parameters<typeof paymentArrangement>[0] = {}) {
  return paymentArrangement({
    serviceAccountId: account.id,
    accountNumber: account.accountNumber,
    ...overrides,
  });
}

/** The 360 with its arrangements tab answered, and every other panel empty. */
function world(arrangements: unknown[] = []) {
  return (url: URL): StubbedResponse | undefined => {
    if (url.pathname === `/api/customers/${record.id}`) return { body: record };
    if (url.pathname === '/api/service-accounts') return { body: [account] };
    if (url.pathname === '/api/service-locations') return { body: [premise] };
    if (url.pathname === `/api/service-accounts/${account.id}/payment-arrangements`) {
      return { body: arrangements };
    }
    if (url.pathname === '/api/payment-arrangements/limits') return { body: arrangementLimits() };
    if (url.pathname === `/api/service-accounts/${account.id}/delinquency`) {
      return {
        body: delinquency({ serviceAccountId: account.id, accountNumber: account.accountNumber }),
      };
    }
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
    { route: `/customers/${record.id}/arrangements` },
  );
}

describe('the arrangements tab', () => {
  it('is a route, so the tab a rep pastes is the tab that opens', async () => {
    renderTab();

    expect(await screen.findByRole('heading', { name: 'Payment arrangements' })).toBeInTheDocument();
  });

  it('states the arrears ceiling before the field rather than after the refusal', async () => {
    // An arrangement records how an EXISTING debt will be paid; it never creates one, so there is
    // nothing above the past-due figure to promise.
    renderTab();

    await screen.findByRole('heading', { name: 'Payment arrangements' });

    expect(await screen.findByText(/\$200\.00 is past due on A-000001/)).toBeInTheDocument();
    expect(screen.getByText(/up to that and no more/)).toBeInTheDocument();
  });

  it('reads the schedule back before anything is committed', async () => {
    renderTab();

    await screen.findByRole('heading', { name: 'Payment arrangements' });

    await userEvent.type(screen.getByLabelText('Amount to arrange'), '100');

    expect(await screen.findByText('$33.34')).toBeInTheDocument();
    expect(screen.getAllByText('$33.33')).toHaveLength(2);
  });

  it('warns that a supervisor is needed before the rep reads the schedule out', async () => {
    // A rep who has promised something they cannot deliver has to ring back. The host decides
    // again — this only decides what the rep is told.
    //
    // The fixture customer is COMMERCIAL, so the figure that trips the warning is the commercial
    // ceiling's and not the residential one's. That is the point of keying the limits on class: the
    // same $2,000 would need a supervisor for a household and does not for a business.
    stub = stubFetch((url) => {
      if (url.pathname === `/api/service-accounts/${account.id}/delinquency`) {
        return {
          body: delinquency({
            serviceAccountId: account.id,
            accountNumber: account.accountNumber,
            arrears: { ...delinquency().arrears, pastDueAmount: 8000 },
          }),
        };
      }

      return world()(url);
    });

    renderWithProviders(
      <Routes>
        <Route path="/customers/:customerId/:tab" element={<CustomerDetailPage />} />
      </Routes>,
      { route: `/customers/${record.id}/arrangements` },
    );

    await screen.findByRole('heading', { name: 'Payment arrangements' });

    const amount = await screen.findByLabelText('Amount to arrange');

    await userEvent.type(amount, '2000');
    expect(screen.queryByText(/go to a supervisor for approval/)).not.toBeInTheDocument();

    await userEvent.clear(amount);
    await userEvent.type(amount, '6000');

    expect(await screen.findByText(/go to a supervisor for approval/)).toBeInTheDocument();
  });

  it('proposes through the host, with the figures the rep typed', async () => {
    renderTab();

    await screen.findByRole('heading', { name: 'Payment arrangements' });

    await userEvent.type(screen.getByLabelText('Amount to arrange'), '150');
    await userEvent.type(screen.getByLabelText('Down payment'), '30');
    await userEvent.click(screen.getByRole('button', { name: 'Propose arrangement' }));

    expect(stub.lastSentBody(`/api/service-accounts/${account.id}/payment-arrangements`)).toMatchObject({
      arrearsBalance: 150,
      downPayment: 30,
      instalmentCount: 3,
    });
  });

  it('shows a proposal as not in force, with the button that changes that', async () => {
    renderTab(world([anArrangement()]));

    expect(await screen.findByRole('heading', { name: 'PA-000001' })).toBeInTheDocument();
    expect(screen.getByText('It is not in force yet, so it protects nothing.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Bring into force' })).toBeInTheDocument();
  });

  it('says what an arrangement in force actually does for the customer', async () => {
    // The one sentence a rep reads out — and `suppressesDisconnection` is the HOST's answer, never
    // something this screen works out from the status.
    renderTab(world([
      anArrangement({
        status: 'Active',
        standing: 'Active',
        suppressesDisconnection: true,
        activatedOn: '2026-09-01',
      }),
    ]));

    expect(
      await screen.findByText(/not disconnected for non-payment while the arrangement is kept/),
    ).toBeInTheDocument();
  });

  it('brings a proposal into force through the host', async () => {
    const arrangement = anArrangement();

    stub = stubFetch((url) => {
      if (
        url.pathname
        === `/api/service-accounts/${account.id}/payment-arrangements/${arrangement.id}/activation`
      ) {
        return { body: { ...arrangement, status: 'Active', standing: 'Active', suppressesDisconnection: true } };
      }

      return world([arrangement])(url);
    });

    renderWithProviders(
      <Routes>
        <Route path="/customers/:customerId/:tab" element={<CustomerDetailPage />} />
      </Routes>,
      { route: `/customers/${record.id}/arrangements` },
    );

    await userEvent.click(await screen.findByRole('button', { name: 'Bring into force' }));

    await waitFor(() =>
      expect(
        stub.lastCall(
          `/api/service-accounts/${account.id}/payment-arrangements/${arrangement.id}/activation`,
        ),
      ).toBeDefined());
  });

  it('explains that an over-limit proposal is waiting on somebody else', async () => {
    renderTab(world([anArrangement({ requiresApproval: true, arrearsBalance: 2000 })]));

    expect(
      await screen.findByText(/a supervisor has to approve it first/),
    ).toBeInTheDocument();
  });

  it('reads a broken arrangement as replaced rather than resumable', async () => {
    renderTab(world([
      anArrangement({ status: 'Broken', standing: 'Broken', closedOn: '2026-10-02' }),
    ]));

    expect(await screen.findByText(/replaced, never resumed/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Bring into force' })).not.toBeInTheDocument();
  });

  it('refuses a second promise beside one already standing', async () => {
    // Two schedules would be two answers to what the customer has agreed to pay.
    renderTab(world([anArrangement({ status: 'Active', standing: 'Active' })]));

    expect(await screen.findByText('One promise at a time')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Propose arrangement' })).not.toBeInTheDocument();
  });

  it('marks a missed instalment on the schedule', async () => {
    renderTab(world([
      anArrangement({
        status: 'Active',
        standing: 'Broken',
        instalments: arrangementInstalments([{ dueDate: '2020-01-01' }]),
      }),
    ]));

    await screen.findByRole('heading', { name: 'PA-000001' });

    // Scoped to the table body: "Due" is also the schedule's first column header, and counting both
    // would make this test pass for the wrong reason.
    const schedule = screen.getByRole('table');

    expect(within(schedule).getByText('Missed')).toBeInTheDocument();
    expect(within(schedule).getAllByRole('cell').filter((cell) => cell.textContent === 'Due')).toHaveLength(2);
  });

  it('shows what has been paid against what is still promised', async () => {
    renderTab(world([
      anArrangement({
        status: 'Active',
        standing: 'Active',
        suppressesDisconnection: true,
        paidAmount: 100,
        outstandingAmount: 200,
        instalments: arrangementInstalments([
          { paidAmount: 100, outstanding: 0, isSettled: true, settledAt: '2026-10-01T09:00:00Z' },
        ]),
      }),
    ]));

    await screen.findByRole('heading', { name: 'PA-000001' });

    const paid = screen.getByText('Paid', { selector: 'p' }).closest('div')!;
    const promised = screen.getByText('Still promised').closest('div')!;

    expect(within(paid).getByText('$100.00')).toBeInTheDocument();
    expect(within(paid).getByText('33% of the schedule')).toBeInTheDocument();
    expect(within(promised).getByText('$200.00')).toBeInTheDocument();
  });

  it('says so plainly when nothing has ever been arranged', async () => {
    renderTab();

    expect(await screen.findByText('No arrangements')).toBeInTheDocument();
  });

  it('asks for nothing until a customer has an account to arrange against', async () => {
    stub = stubFetch((url) => {
      if (url.pathname === '/api/service-accounts') return { body: [] };

      return world()(url);
    });

    renderWithProviders(
      <Routes>
        <Route path="/customers/:customerId/:tab" element={<CustomerDetailPage />} />
      </Routes>,
      { route: `/customers/${record.id}/arrangements` },
    );

    expect(await screen.findByText('No service accounts')).toBeInTheDocument();
    expect(stub.lastCall('/api/payment-arrangements/limits')).toBeUndefined();
  });
});
