import { describe, expect, it } from 'vitest';
import type { Bill } from '@/api/billing';
import type { ServiceAccount, ServiceAccountHistoryEntry } from '@/api/customers';
import {
  buildCustomerTimeline,
  countsTowardBalance,
  customerBalance,
  lastSettledPayment,
  sortAccounts,
  timelineKinds,
  type CustomerTimelineSources,
} from './customer-360';
import { bill, payment } from '@/test/revenue-cycle-fixtures';
import { serviceAccount } from '@/test/registry-fixtures';

/**
 * The 360° page's two disputable claims, tested without a DOM: what a customer owes, and what order
 * things happened in. Milliseconds each, no network, no React (CONVENTIONS.md ⚡).
 */

const account = serviceAccount();

/** The transitions the history endpoint returns for `account` — a list row carries none. */
const history: ServiceAccountHistoryEntry[] = account.history;

function sources(overrides: Partial<CustomerTimelineSources> = {}): CustomerTimelineSources {
  return {
    accounts: [account],
    historyByAccountId: new Map([[account.id, history]]),
    bills: [bill()],
    payments: [payment()],
    ...overrides,
  };
}

/** A bill with an adjustment on it, as `?includeAdjustments=true` returns one. */
function adjustedBill(overrides: Partial<Bill> = {}): Bill {
  return bill({
    adjustmentTotal: -20,
    amountDue: 43.62,
    balance: 43.62,
    adjustments: [
      {
        id: '0192f000-0000-7000-8000-000000000d01',
        sequence: 1,
        kind: 'Credit',
        amount: -20,
        amountDueAfter: 43.62,
        reason: 'Estimated read corrected.',
        actorId: 'demo:officer',
        actorName: 'Wes Store (demo)',
        recordedAt: '2026-08-27T02:00:00+00:00',
      },
    ],
    ...overrides,
  });
}

describe('buildCustomerTimeline', () => {
  it('merges four sources into one feed, newest first', () => {
    const entries = buildCustomerTimeline(sources({ bills: [adjustedBill()] }));

    expect(entries.map((entry) => entry.kind)).toEqual([
      'adjustment', // 27 Aug
      'payment', // 25 Aug, 01:00
      'bill', // 25 Aug, issued (a date, so midnight)
      'account', // 14 Feb
      'account', // 12 Feb
    ]);

    const instants = entries.map((entry) => Date.parse(entry.occurredAt));
    expect(instants).toEqual(instants.toSorted((left, right) => right - left));
  });

  /**
   * The tie-break the WP asks about. Two bills issued on the same day carry the same instant to
   * the millisecond, because `issuedOn` is a `DateOnly`.
   */
  it('orders equal timestamps by a total rule rather than by arrival', () => {
    const first = bill({ id: '0192f000-0000-7000-8000-00000000aa01', billNumber: 'BIL-000010' });
    const second = bill({ id: '0192f000-0000-7000-8000-00000000aa02', billNumber: 'BIL-000011' });

    const forwards = buildCustomerTimeline(sources({ bills: [first, second], payments: [] }));
    const backwards = buildCustomerTimeline(sources({ bills: [second, first], payments: [] }));

    expect(forwards.map((entry) => entry.id)).toEqual(backwards.map((entry) => entry.id));

    // Later id first: these are Guid v7, so the second bill raised is the newer event.
    const numbers = forwards.filter((entry) => entry.kind === 'bill').map((entry) => entry.title);
    expect(numbers[0]).toContain('BIL-000011');
    expect(numbers[1]).toContain('BIL-000010');
  });

  /** Same instant, different kinds: the causal order decides, reversed because the feed runs back. */
  it('puts the payment above the bill when both land on the same instant', () => {
    const sameInstant = '2026-08-25T00:00:00+00:00';

    const entries = buildCustomerTimeline(
      sources({
        accounts: [],
        historyByAccountId: new Map(),
        bills: [bill({ issuedOn: '2026-08-25' })],
        payments: [payment({ settledAt: sameInstant })],
      }),
    );

    expect(entries.map((entry) => entry.kind)).toEqual(['payment', 'bill']);
  });

  it('reordering the kinds is what would reorder those clusters', () => {
    // The comment on `timelineKinds` claims the order is load-bearing; this is that claim, asserted.
    expect([...timelineKinds]).toEqual(['account', 'bill', 'adjustment', 'payment']);
  });

  /** A draft is owed by nobody and was never sent, so nothing happened to the customer. */
  it('leaves a draft bill off the feed entirely', () => {
    const entries = buildCustomerTimeline(
      sources({ bills: [bill({ status: 'Draft', issuedOn: null, dueDate: null })], payments: [] }),
    );

    expect(entries.some((entry) => entry.kind === 'bill')).toBe(false);
  });

  /** It was issued that day and the customer was told so; withdrawing it does not unsay that. */
  it('keeps the issuing of a bill that was later cancelled', () => {
    const entries = buildCustomerTimeline(
      sources({ bills: [bill({ status: 'Cancelled', balance: 0, isOutstanding: false })], payments: [] }),
    );

    expect(entries.find((entry) => entry.kind === 'bill')?.title).toContain('BIL-000001 issued');
  });

  it('makes each correction an entry of its own, worded by which way it went', () => {
    const credited = buildCustomerTimeline(sources({ bills: [adjustedBill()], payments: [] }));
    const charged = buildCustomerTimeline(
      sources({
        bills: [
          adjustedBill({
            adjustments: [{ ...adjustedBill().adjustments[0], kind: 'Charge', amount: 20 }],
          }),
        ],
        payments: [],
      }),
    );

    expect(credited.find((entry) => entry.kind === 'adjustment')?.title).toBe(
      'Bill BIL-000001 credited $20.00',
    );
    expect(charged.find((entry) => entry.kind === 'adjustment')?.title).toBe(
      'Bill BIL-000001 charged $20.00',
    );

    // The reason a rep reads back, not the running total.
    expect(credited.find((entry) => entry.kind === 'adjustment')?.detail).toBe(
      'Estimated read corrected.',
    );
  });

  /**
   * A bill fetched without `includeAdjustments` carries a running `adjustmentTotal` and no entries.
   * A total has no date, so inventing an entry from it would be placing an event at a guessed time.
   */
  it('contributes no correction entries when the bills were fetched without them', () => {
    const entries = buildCustomerTimeline(
      sources({ bills: [bill({ adjustmentTotal: -20, amountDue: 43.62, adjustments: [] })], payments: [] }),
    );

    expect(entries.some((entry) => entry.kind === 'adjustment')).toBe(false);
  });

  /** Failure path: a refusal is the answer to "why am I still being chased", so it is on the feed. */
  it('shows a declined payment rather than dropping it', () => {
    const declined = payment({
      id: '0192f000-0000-7000-8000-000000000c02',
      paymentNumber: 'PAY-000002',
      status: 'Declined',
      outcome: 'Declined',
      isSettled: false,
      settledAt: null,
      providerMessage: 'The card was declined.',
    });

    const entry = buildCustomerTimeline(sources({ payments: [declined] })).find(
      (candidate) => candidate.kind === 'payment',
    );

    expect(entry?.title).toBe('Payment declined — $63.62');
    expect(entry?.detail).toContain('The card was declined.');
    expect(entry?.tone).toBe('danger');

    // It never settled, so it is timed at the request — dropping it for want of a `settledAt` is
    // exactly how a decline would go missing.
    expect(entry?.occurredAt).toBe(declined.requestedAt);
  });

  it('names the opening line and the transitions, each against its account', () => {
    const titles = buildCustomerTimeline(sources()).map((entry) => entry.title);

    expect(titles).toContain('Account A-000001 opened');
    expect(titles).toContain('Account A-000001: Pending → Active');
  });

  /**
   * The history arrives per account, one request each. An account whose request has not answered
   * contributes nothing — which is not the same as an account with no history, and both render as
   * an absence rather than as a throw.
   */
  it('contributes nothing for an account whose history has not arrived', () => {
    const entries = buildCustomerTimeline(sources({ historyByAccountId: new Map() }));

    expect(entries.some((entry) => entry.kind === 'account')).toBe(false);
    expect(entries.length).toBeGreaterThan(0);
  });

  it('renders a bill at day precision and everything else at instant precision', () => {
    const entries = buildCustomerTimeline(sources({ bills: [adjustedBill()] }));

    for (const entry of entries) {
      expect(entry.precision).toBe(entry.kind === 'bill' ? 'date' : 'time');
    }
  });

  /** Two modules' ids are two modules' ids; the feed keys on them, so it prefixes them. */
  it('prefixes every id with its kind', () => {
    const entries = buildCustomerTimeline(sources({ bills: [adjustedBill()] }));

    expect(new Set(entries.map((entry) => entry.id)).size).toBe(entries.length);
    for (const entry of entries) {
      expect(entry.id.startsWith(`${entry.kind}:`)).toBe(true);
    }
  });

  /** Failure path: a customer nothing has happened to is a real state, not an error. */
  it('is empty for a customer with no accounts, bills or payments', () => {
    expect(
      buildCustomerTimeline({
        accounts: [],
        historyByAccountId: new Map(),
        bills: [],
        payments: [],
      }),
    ).toEqual([]);
  });
});

describe('customerBalance', () => {
  it('is bills issued, plus corrections, less what has been paid', () => {
    const balance = customerBalance([
      bill({ id: 'b1', totalAmount: 100, adjustmentTotal: 0, amountDue: 100, amountPaid: 40, balance: 60 }),
      adjustedBill({ id: 'b2', totalAmount: 63.62, amountPaid: 0 }),
    ]);

    expect(balance.billed).toBe(163.62);
    expect(balance.adjustments).toBe(-20);
    expect(balance.netBilled).toBe(143.62);
    expect(balance.paid).toBe(40);
    expect(balance.outstanding).toBe(103.62);
  });

  /**
   * The same figure by the other route. Every bill already knows its own balance, and the page's
   * total has to be the sum of them — the day those two disagree, one of the two is wrong and a
   * rep is quoting it down a phone.
   */
  it('agrees with the sum of the bills own balances', () => {
    const bills = [
      bill({ id: 'b1', totalAmount: 100, adjustmentTotal: 0, amountDue: 100, amountPaid: 40, balance: 60 }),
      adjustedBill({ id: 'b2' }),
      bill({ id: 'b3', totalAmount: 12.05, amountDue: 12.05, amountPaid: 12.05, balance: 0, status: 'Paid' }),
    ];

    const summed = bills.reduce((total, row) => total + row.balance, 0);

    expect(customerBalance(bills).outstanding).toBeCloseTo(summed, 10);
  });

  it('leaves out the bills nobody owes', () => {
    const bills = [
      bill({ id: 'b1', totalAmount: 100, amountDue: 100, amountPaid: 0, balance: 100 }),
      bill({ id: 'b2', status: 'Draft', issuedOn: null, totalAmount: 500, amountDue: 500, balance: 500 }),
      bill({ id: 'b3', status: 'Cancelled', totalAmount: 900, amountDue: 900, balance: 900 }),
    ];

    expect(customerBalance(bills).outstanding).toBe(100);
    expect(customerBalance(bills).billed).toBe(100);

    expect(countsTowardBalance(bills[0])).toBe(true);
    expect(countsTowardBalance(bills[1])).toBe(false);
    expect(countsTowardBalance(bills[2])).toBe(false);
  });

  it('counts the bills still owing and totals the ones past due', () => {
    const balance = customerBalance([
      bill({ id: 'b1', amountPaid: 63.62, balance: 0, status: 'Paid' }),
      bill({ id: 'b2', balance: 63.62, status: 'Overdue' }),
      bill({ id: 'b3', balance: 20, amountPaid: 43.62, status: 'PartiallyPaid' }),
    ]);

    expect(balance.outstandingBills).toBe(2);
    expect(balance.overdue).toBe(63.62);
  });

  /** Summed in cents, so a year of bills does not drift into a balance nobody can reconcile. */
  it('does not accumulate floating-point residue', () => {
    const bills = Array.from({ length: 3 }, (_, index) =>
      bill({ id: `b${index}`, totalAmount: 0.1, amountDue: 0.1, amountPaid: 0, balance: 0.1 }),
    );

    // Added as parsed numbers these come to 0.30000000000000004, which is what a rep would read.
    expect(bills.reduce((total, row) => total + row.balance, 0)).not.toBe(0.3);
    expect(customerBalance(bills).outstanding).toBe(0.3);
  });

  /** Failure path: a prospect nobody has billed is five honest zeros, not a throw. */
  it('is all zeros for a customer with no bills', () => {
    expect(customerBalance([])).toEqual({
      billed: 0,
      adjustments: 0,
      netBilled: 0,
      paid: 0,
      outstanding: 0,
      outstandingBills: 0,
      overdue: 0,
    });
  });
});

describe('lastSettledPayment', () => {
  it('is the most recent payment the utility actually holds', () => {
    const older = payment({ id: 'p1', settledAt: '2026-07-25T01:00:00+00:00' });
    const newer = payment({ id: 'p2', settledAt: '2026-08-25T01:00:00+00:00' });

    expect(lastSettledPayment([older, newer])?.id).toBe('p2');
    expect(lastSettledPayment([newer, older])?.id).toBe('p2');
  });

  /** A tile saying "last payment: $63.62" over a decline would be a lie told in a confident font. */
  it('ignores an attempt that was refused', () => {
    const declined = payment({
      id: 'p3',
      status: 'Declined',
      isSettled: false,
      settledAt: null,
      requestedAt: '2026-09-01T01:00:00+00:00',
    });

    expect(lastSettledPayment([declined])).toBeUndefined();
  });

  it('is undefined when nothing has settled', () => {
    expect(lastSettledPayment([])).toBeUndefined();
  });
});

describe('sortAccounts', () => {
  it('puts open accounts above closed ones, then orders by number', () => {
    const rows: ServiceAccount[] = [
      serviceAccount({ id: 'a3', accountNumber: 'A-000003', status: 'Closed' }),
      serviceAccount({ id: 'a2', accountNumber: 'A-000002', status: 'Active' }),
      serviceAccount({ id: 'a1', accountNumber: 'A-000001', status: 'Disconnected' }),
    ];

    expect(sortAccounts(rows).map((row) => row.accountNumber)).toEqual([
      'A-000001',
      'A-000002',
      'A-000003',
    ]);
  });

  it('does not mutate what it was given', () => {
    const rows = [
      serviceAccount({ id: 'a2', accountNumber: 'A-000002' }),
      serviceAccount({ id: 'a1', accountNumber: 'A-000001' }),
    ];

    sortAccounts(rows);

    expect(rows.map((row) => row.accountNumber)).toEqual(['A-000002', 'A-000001']);
  });
});
