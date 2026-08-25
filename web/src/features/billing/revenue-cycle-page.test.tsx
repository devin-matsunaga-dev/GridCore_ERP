import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { stubFetch, type FetchStub, type StubbedResponse } from '@/test/api-stub';
import { customer, meter, serviceAccount, serviceLocation } from '@/test/registry-fixtures';
import {
  bill,
  billingRun,
  billTotal,
  cashReceiptEntry,
  declinedPaymentResult,
  journalEntry,
  paidBill,
  readingCycle,
  receivables,
  takePaymentResult,
  trialBalance,
} from '@/test/revenue-cycle-fixtures';
import { renderWithProviders } from '@/test/render';
import { RevenueCyclePage } from './revenue-cycle-page';

/**
 * The demonstration walk, driven end to end against a stubbed host.
 *
 * The assertions are on the **URLs and bodies the client produced** as much as on what reached the
 * screen: this walk's whole value is that each step performs the real act, so a step that quietly
 * stopped calling its endpoint would otherwise still look right.
 */

const registered = customer({ status: 'Prospect', statusChangedAt: null, statusReason: null });
const premise = serviceLocation();
const pending = serviceAccount({ status: 'Pending', serviceStartedAt: null, history: [] });
const active = serviceAccount();
const inStore = meter({ status: 'InStore', isFitted: false, serviceLocationId: null, serviceLocation: null });
const fitted = meter();

let stub: FetchStub;
/** Every request body the client sent, keyed by path — the other half of what a step did. */
let bodies: { path: string; body: unknown }[];

afterEach(() => stub?.restore());

/** A host that answers the whole walk with an approved card payment and a settled bill. */
function happyPath(url: URL): StubbedResponse | undefined {
  switch (url.pathname) {
    case '/api/customers':
      return { body: registered };
    case '/api/service-locations':
      return { body: premise };
    case '/api/service-accounts':
      return { body: pending };
    case `/api/service-accounts/${pending.id}/start`:
      return { body: active };
    case '/api/meters':
      return { body: inStore };
    case `/api/meters/${inStore.id}/assign`:
      return { body: fitted };
    case '/api/meter-readings/cycles':
      return { body: readingCycle() };
    case '/api/bills/runs':
      return { body: billingRun() };
    case `/api/bills/${bill().id}/issue`:
      return { body: bill() };
    case '/api/payments':
      return { body: takePaymentResult() };
    case `/api/bills/${bill().id}`:
      return { body: paidBill() };
    case '/api/finance/journal-entries':
      return { body: [journalEntry(), cashReceiptEntry()] };
    case '/api/finance/accounts-receivable':
      return { body: receivables() };
    case '/api/finance/trial-balance':
      return { body: trialBalance() };
    default:
      return undefined;
  }
}

function renderPage(respond: (url: URL) => StubbedResponse | undefined = happyPath) {
  bodies = [];

  stub = stubFetch((url) => respond(url));

  // `stubFetch` records the URL; the body is the other half of what a step did, so it is captured
  // here from the same spy rather than by a second one.
  const original = globalThis.fetch as unknown as (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;

  globalThis.fetch = ((input: RequestInfo | URL, init?: RequestInit) => {
    const href = typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url;

    if (init?.body) {
      bodies.push({ path: new URL(href, 'http://localhost').pathname, body: JSON.parse(String(init.body)) });
    }

    return original(input, init);
  }) as typeof globalThis.fetch;

  return renderWithProviders(<RevenueCyclePage />, { route: '/billing' });
}

/**
 * One step's card. Several figures legitimately appear more than once on a finished walk — the
 * bill's status is a pill on the payment card and a word on the accounting one — so an assertion
 * about a step scopes itself to that step rather than to the page.
 */
function stepCard(id: string): HTMLElement {
  const card = document.querySelector(`[data-step="${id}"]`);

  if (!card) throw new Error(`The walk has no card for step '${id}'.`);

  return card as HTMLElement;
}

/** The body the client posted to `path`, most recent first. */
function bodyFor(path: string): Record<string, unknown> | undefined {
  return bodies.findLast((call) => call.path === path)?.body as Record<string, unknown> | undefined;
}

/** Walks the steps that have no inputs of interest, up to and including issuing the bill. */
async function walkToTheBill(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole('button', { name: /register customer and premise/i }));
  expect(await screen.findByText(registered.accountNumber)).toBeInTheDocument();

  await user.click(screen.getByRole('button', { name: /open and energise the account/i }));
  expect(await screen.findByText(active.accountNumber)).toBeInTheDocument();

  await user.click(screen.getByRole('button', { name: /register and fit the meter/i }));
  expect(await screen.findByText(fitted.meterNumber)).toBeInTheDocument();

  await user.click(screen.getByRole('button', { name: /run the reading cycle/i }));
  expect(await screen.findByText('Simulated meter reading provider')).toBeInTheDocument();

  await user.click(screen.getByRole('button', { name: /run billing and issue the bill/i }));
  expect(await screen.findByText(bill().billNumber)).toBeInTheDocument();
}

describe('RevenueCyclePage', () => {
  it('opens on the first step with every later one visible but waiting', () => {
    renderPage();

    // A demonstration should show where it is going before it gets there.
    expect(screen.getByRole('heading', { name: 'Register the customer' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Read the books' })).toBeInTheDocument();
    expect(screen.getByText('0 of 7 done')).toBeInTheDocument();
  });

  it('names all nine of SPEC.md’s steps across the seven cards', () => {
    renderPage();

    for (const step of [
      'Create Customer',
      'Create Service Account',
      'Assign Meter',
      'Generate Simulated Reading',
      'Calculate Consumption',
      'Generate Bill',
      'Run Simulated Payment',
      'Update Balance',
      'Generate Accounting Entries',
    ]) {
      expect(screen.getByText(new RegExp(step))).toBeInTheDocument();
    }
  });

  it('registers the customer and the premise as two records, not one', async () => {
    const user = userEvent.setup();
    renderPage();

    await user.click(screen.getByRole('button', { name: /register customer and premise/i }));

    expect(await screen.findByText(registered.accountNumber)).toBeInTheDocument();
    expect(screen.getByText(premise.locationCode)).toBeInTheDocument();

    // Two endpoints, two registries.
    expect(stub.lastCall('/api/customers')).toBeDefined();
    expect(stub.lastCall('/api/service-locations')).toBeDefined();
    expect(bodyFor('/api/customers')).toMatchObject({ name: 'Reyes Family Residence', class: 'Residential' });
  });

  it('energises the account as a second act rather than opening it energised', async () => {
    const user = userEvent.setup();
    renderPage();

    await user.click(screen.getByRole('button', { name: /register customer and premise/i }));
    await screen.findByText(registered.accountNumber);

    await user.click(screen.getByRole('button', { name: /open and energise the account/i }));

    expect(await screen.findByText(active.accountNumber)).toBeInTheDocument();

    // The start endpoint is what moves Pending → Active, and the billing run refuses an account
    // that never reached it.
    expect(stub.lastCall(`/api/service-accounts/${pending.id}/start`)).toBeDefined();
  });

  it('fits the meter to the premise and never sends an account id', async () => {
    const user = userEvent.setup();
    renderPage();

    await user.click(screen.getByRole('button', { name: /register customer and premise/i }));
    await screen.findByText(registered.accountNumber);
    await user.click(screen.getByRole('button', { name: /open and energise the account/i }));
    await screen.findByText(active.accountNumber);

    await user.click(screen.getByRole('button', { name: /register and fit the meter/i }));

    expect(await screen.findByText(fitted.meterNumber)).toBeInTheDocument();

    // WP-2.1's rule, asserted from the client's side: a meter is fitted to a PLACE.
    const assigned = bodyFor(`/api/meters/${inStore.id}/assign`);

    expect(assigned).toMatchObject({ serviceLocationId: premise.id, installationReading: 4200 });
    expect(assigned).not.toHaveProperty('serviceAccountId');
  });

  it('runs the cycle through the provider and reports the whole batch, not just this premise', async () => {
    const user = userEvent.setup();
    renderPage();

    await user.click(screen.getByRole('button', { name: /register customer and premise/i }));
    await screen.findByText(registered.accountNumber);
    await user.click(screen.getByRole('button', { name: /open and energise the account/i }));
    await screen.findByText(active.accountNumber);
    await user.click(screen.getByRole('button', { name: /register and fit the meter/i }));
    await screen.findByText(fitted.meterNumber);

    await user.click(screen.getByRole('button', { name: /run the reading cycle/i }));

    // The provider name comes off the batch, so what is on screen is what actually read the meters.
    expect(await screen.findByText('Simulated meter reading provider')).toBeInTheDocument();
    // A cycle reads the whole estate; hiding that would be demonstrating a different product.
    expect(screen.getByText('24')).toBeInTheDocument();
    // And this premise's reading is picked out of it, consumption and all.
    expect(screen.getByText(`447 ${'kWh'}`)).toBeInTheDocument();

    expect(bodyFor('/api/meter-readings/cycles')).toMatchObject({ seed: 4471 });
  });

  it('bills the cycle, issues this account’s bill, and shows why the run skipped the rest', async () => {
    const user = userEvent.setup();
    renderPage();

    await walkToTheBill(user);

    // Issuing is the act that makes the money owed — and the act that reaches Finance.
    expect(stub.lastCall(`/api/bills/${bill().id}/issue`)).toBeDefined();
    expect(bodyFor('/api/bills/runs')).toMatchObject({ cycleCode: readingCycle().cycleCode });

    // The lines add up to the printed total, which is the first thing a customer checks.
    expect(screen.getByText('$63.62')).toBeInTheDocument();
    expect(screen.getByText(/No open service account at the premise/)).toBeInTheDocument();
  });

  it('takes the payment and waits for Billing’s consumer to move the balance', async () => {
    const user = userEvent.setup();
    renderPage();

    await walkToTheBill(user);

    await user.click(screen.getByRole('button', { name: /take the payment/i }));

    expect(await screen.findByText('PAY-000001')).toBeInTheDocument();

    // The amount defaults to what is outstanding, and the instrument is sent because it is a card.
    expect(bodyFor('/api/payments')).toMatchObject({
      billId: bill().id,
      amount: billTotal,
      method: 'card',
      instrument: '•••• 4242',
    });

    // The balance moved in Billing's schema, which the walk learns by re-reading the bill — there
    // is no response that could have carried it.
    await waitFor(() => expect(stub.lastCall(`/api/bills/${bill().id}`)).toBeDefined());
    await waitFor(() => expect(within(stepCard('payment')).getByText('$0.00')).toBeInTheDocument());
    expect(within(stepCard('payment')).getAllByText('Paid').length).toBeGreaterThan(0);
  });

  it('reads the ledger back and says the numbers reconcile', async () => {
    const user = userEvent.setup();
    renderPage();

    await walkToTheBill(user);
    await user.click(screen.getByRole('button', { name: /take the payment/i }));
    await screen.findByText('PAY-000001');

    expect(await screen.findByText('The numbers reconcile')).toBeInTheDocument();

    // Both entries, posted by consumers from events — never by this screen.
    const books = within(stepCard('accounting'));

    expect(books.getAllByText(/JRN-000001/).length).toBeGreaterThan(0);
    expect(books.getAllByText(/JRN-000002/).length).toBeGreaterThan(0);

    // Scoped to this account: the ledger is asked what it owes for THIS account, not globally.
    expect(stub.lastCall('/api/finance/accounts-receivable')?.searchParams.get('serviceAccountId')).toBe(active.id);
    expect(stub.lastCall('/api/finance/journal-entries')?.searchParams.get('serviceAccountId')).toBe(active.id);

    expect(screen.getByText('7 of 7 done')).toBeInTheDocument();
  });

  /**
   * Failure path, and the one that matters most: a refused payment must not settle anything. The
   * bill is still owed, the ledger says so, and the walk still reaches the books.
   */
  it('carries on to the books when the provider refuses, with the bill still owed', async () => {
    const owed = receivables({
      rows: [{ ...receivables().rows[0]!, settled: 0, outstanding: billTotal, postingCount: 1 }],
      totalSettled: 0,
      totalOutstanding: billTotal,
    });

    const user = userEvent.setup();

    renderPage((url) => {
      if (url.pathname === '/api/payments') return { body: declinedPaymentResult() };
      // Unmoved: no event was published, so no consumer can have touched it.
      if (url.pathname === `/api/bills/${bill().id}`) return { body: bill() };
      if (url.pathname === '/api/finance/journal-entries') return { body: [journalEntry()] };
      if (url.pathname === '/api/finance/accounts-receivable') return { body: owed };
      return happyPath(url);
    });

    await walkToTheBill(user);
    await user.click(screen.getByRole('button', { name: /take the payment/i }));

    await waitFor(() => expect(within(stepCard('payment')).getAllByText('Declined').length).toBeGreaterThan(0));
    expect(screen.getByText(/Nothing was published, so nothing moved/)).toBeInTheDocument();

    // The books agree with the bill: still owed, and still balanced.
    expect(await screen.findByText('The numbers reconcile')).toBeInTheDocument();

    const reconciliation = screen.getByRole('table', {
      name: /What the billing register says against what the general ledger says/i,
    });

    const stillOwed = within(reconciliation).getByText('Still owed').closest('tr')!;

    expect(within(stillOwed).getAllByText('$63.62')).toHaveLength(2);
  });

  /** Failure path: the walk must not claim agreement the ledger does not support. */
  it('says the numbers do not reconcile when the ledger disagrees with the bill', async () => {
    const wrong = receivables({
      rows: [{ ...receivables().rows[0]!, settled: 0, outstanding: billTotal }],
      totalSettled: 0,
      totalOutstanding: billTotal,
    });

    const user = userEvent.setup();

    // The bill says it is paid; the ledger says nothing was settled. Exactly the disagreement this
    // screen exists to surface rather than smooth over.
    renderPage((url) =>
      url.pathname === '/api/finance/accounts-receivable' ? { body: wrong } : happyPath(url),
    );

    await walkToTheBill(user);
    await user.click(screen.getByRole('button', { name: /take the payment/i }));
    await screen.findByText('PAY-000001');

    expect(await screen.findByText('The numbers do not reconcile')).toBeInTheDocument();
  });

  it('starts again from an empty walk', async () => {
    const user = userEvent.setup();
    renderPage();

    await user.click(screen.getByRole('button', { name: /register customer and premise/i }));
    await screen.findByText(registered.accountNumber);
    expect(screen.getByText('1 of 7 done')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /start again/i }));

    expect(screen.getByText('0 of 7 done')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /register customer and premise/i })).toBeInTheDocument();
  });
});
