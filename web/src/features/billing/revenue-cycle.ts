import type { Bill, BillingRun } from '@/api/billing';
import type { Customer, ServiceAccount, ServiceLocation } from '@/api/customers';
import type { JournalEntry, Receivables, TrialBalance } from '@/api/finance';
import type { Meter, MeterReading, ReadingCycle } from '@/api/metering';
import type { TakePaymentResult } from '@/api/payments';
import { centsEqual } from '@/lib/money';

/**
 * The revenue cycle as a walk: which step comes next, what each one has produced, and whether the
 * books agree with the bill at the end of it.
 *
 * Pure, and deliberately so — no React, no DOM, no network. This is where the demonstration's
 * *logic* lives, so it can be tested exhaustively in milliseconds (CONVENTIONS.md ⚡), the same
 * call `useTableState` made for sorting and paging. The page is what renders it.
 */

/** The steps of the walk, in order. */
export const revenueCycleStepIds = [
  'customer',
  'service-account',
  'meter',
  'reading',
  'bill',
  'payment',
  'accounting',
] as const;

export type RevenueCycleStepId = (typeof revenueCycleStepIds)[number];

export type RevenueCycleStep = {
  id: RevenueCycleStepId;
  /** What the step is called on screen. */
  title: string;
  /** What it proves, in one line — the demonstration's own commentary. */
  summary: string;
  /**
   * The steps of SPEC.md's Revenue Cycle this one covers. Several pair naturally: consumption is
   * computed as a reading is recorded, and a balance moves because a payment was approved.
   */
  specSteps: string[];
};

/**
 * SPEC.md's nine steps, as the seven acts somebody actually performs. Consumption is not a button —
 * it is what Metering works out the moment a reading lands — and neither is updating a balance,
 * which happens in Billing's schema because Payments published a fact.
 */
export const revenueCycleSteps: RevenueCycleStep[] = [
  {
    id: 'customer',
    title: 'Register the customer',
    summary: 'A customer and the premise they are served at — two registries, two records.',
    specSteps: ['Create Customer'],
  },
  {
    id: 'service-account',
    title: 'Open and energise the account',
    summary: 'The account pairs a customer with a premise. Energising it is a separate act.',
    specSteps: ['Create Service Account'],
  },
  {
    id: 'meter',
    title: 'Fit the meter',
    summary: 'Fitted to the premise, never to the account, with the reading its dials went on at.',
    specSteps: ['Assign Meter'],
  },
  {
    id: 'reading',
    title: 'Read the meters',
    summary: 'The simulator reads every fitted meter; Metering works out what each reading means.',
    specSteps: ['Generate Simulated Reading', 'Calculate Consumption'],
  },
  {
    id: 'bill',
    title: 'Raise and issue the bill',
    summary: 'The rate engine prices the cycle. Issuing is what makes the money owed.',
    specSteps: ['Generate Bill'],
  },
  {
    id: 'payment',
    title: 'Take the payment',
    summary: 'The sandbox answers, and Billing reduces the balance from the fact it published.',
    specSteps: ['Run Simulated Payment', 'Update Balance'],
  },
  {
    id: 'accounting',
    title: 'Read the books',
    summary: 'Finance posted both sides from events alone. The ledger and the bill must agree.',
    specSteps: ['Generate Accounting Entries'],
  },
];

/** What the walk has produced so far. Every field is filled in by the step that produces it. */
export type RevenueCycleState = {
  customer?: Customer;
  location?: ServiceLocation;
  /** The account once it is energised — a pending one has not finished the step. */
  account?: ServiceAccount;
  /** The meter once it is fitted. */
  meter?: Meter;
  /** The cycle the simulator ran, whole-estate figures and all. */
  cycle?: ReadingCycle;
  /** This premise's reading out of that cycle. */
  reading?: MeterReading;
  /** What the billing run came to across the cycle. */
  billingRun?: BillingRun;
  /** This account's bill, once issued. */
  bill?: Bill;
  /** The payment attempt, approved or refused. */
  payment?: TakePaymentResult;
  /**
   * The bill re-read after Billing's consumer has had the approval. A refused payment leaves this
   * identical to `bill`, which is the point of re-reading it either way.
   */
  settledBill?: Bill;
  /** The entries Finance posted, once the walk has watched them arrive. */
  postedEntries?: JournalEntry[];
};

export type RevenueCycleStepStatus = 'done' | 'active' | 'waiting';

/**
 * Whether a step has produced what it exists to produce.
 *
 * Note what each one waits for: the account step is not done until the account is **Active**,
 * because an account that was opened and never energised cannot be billed; and the payment step is
 * done whether the provider approved or refused, because a refusal is an answer and the walk
 * carries on to the books either way.
 */
export function isStepComplete(state: RevenueCycleState, id: RevenueCycleStepId): boolean {
  switch (id) {
    case 'customer':
      return Boolean(state.customer && state.location);
    case 'service-account':
      return state.account?.status === 'Active';
    case 'meter':
      return Boolean(state.meter?.isFitted);
    case 'reading':
      return Boolean(state.reading);
    case 'bill':
      return Boolean(state.bill);
    case 'payment':
      return Boolean(state.payment);
    case 'accounting':
      return Boolean(state.postedEntries?.length);
  }
}

/**
 * The step the walk is on: the first one that is not complete.
 *
 * Deliberately the FIRST incomplete step rather than the one after the last complete one. They are
 * the same while the walk runs forward, and they differ only if state arrived out of order — in
 * which case pointing at the earliest gap is the answer that lets somebody fill it.
 */
export function activeStepId(state: RevenueCycleState): RevenueCycleStepId | null {
  return revenueCycleStepIds.find((id) => !isStepComplete(state, id)) ?? null;
}

export function stepStatus(state: RevenueCycleState, id: RevenueCycleStepId): RevenueCycleStepStatus {
  if (isStepComplete(state, id)) return 'done';

  return activeStepId(state) === id ? 'active' : 'waiting';
}

/** How far along the walk is, for a progress line. */
export function completedStepCount(state: RevenueCycleState): number {
  return revenueCycleStepIds.filter((id) => isStepComplete(state, id)).length;
}

export function isCycleComplete(state: RevenueCycleState): boolean {
  return activeStepId(state) === null;
}

/**
 * A cycle code no previous run can have used.
 *
 * The code is the idempotency key behind `ux_meter_readings_meter_cycle` and `ux_bills_account_cycle`:
 * running a cycle twice under one code is a 409 naming it, which is correct for a reading run and
 * useless for a demonstration somebody wants to give twice in an afternoon. Minute resolution, and
 * within the 32 characters the column holds.
 */
export function nextCycleCode(now: Date = new Date()): string {
  const stamp = [
    now.getFullYear(),
    pad(now.getMonth() + 1),
    pad(now.getDate()),
    '-',
    pad(now.getHours()),
    pad(now.getMinutes()),
  ].join('');

  return `DEMO-${stamp}`;
}

function pad(value: number): string {
  return String(value).padStart(2, '0');
}

/**
 * The postings this walk should have caused, by the source Finance stamps on an entry.
 *
 * A charge always: issuing the bill published `BillIssued`, and Finance raised the receivable from
 * that alone. A receipt only if the provider approved — a refusal publishes nothing, so waiting for
 * a cash entry that will never come would leave the demonstration hanging on a step that is
 * already, correctly, finished.
 */
export function expectedPostingSources(state: RevenueCycleState): string[] {
  if (!state.bill) return [];

  return state.payment?.approved
    ? [postingSources.billIssued, postingSources.paymentApproved]
    : [postingSources.billIssued];
}

/** The `Source` values Finance stamps on an entry — `FinancePostings` on the host. */
export const postingSources = {
  billIssued: 'billing.bill_issued',
  billAdjusted: 'billing.bill_adjusted',
  paymentApproved: 'payments.payment_approved',
} as const;

/** Whether every posting the walk should have caused has actually arrived. */
export function hasPosted(entries: readonly JournalEntry[] | undefined, sources: readonly string[]): boolean {
  if (sources.length === 0) return false;

  return sources.every((source) => entries?.some((entry) => entry.source === source));
}

/** One figure Billing states and Finance ought to agree with. */
export type ReconciliationCheck = {
  id: string;
  label: string;
  /** What the billing register says. */
  billing: number;
  /** What the general ledger says, read back through the receivables view. */
  finance: number;
  agrees: boolean;
  /** Why the two are the same figure, for a reader who should not have to take it on trust. */
  note: string;
};

export type Reconciliation = {
  checks: ReconciliationCheck[];
  /** Invariant 3, straight off the trial balance. */
  ledgerBalances: boolean;
  /** True only when every check agrees AND the ledger balances. */
  reconciles: boolean;
};

/**
 * Whether the books say what the bill says.
 *
 * This is WORK_PACKAGES.md's "numbers reconcile", as three figures rather than a slogan. Finance
 * was never told any of them: it heard `BillIssued` and `PaymentApproved` and posted from those
 * alone, so agreement here is two independent modules arriving at the same answer, not one reading
 * the other's total.
 *
 * Compared in **cents**, not with `===`. These amounts are `decimal` on the wire and `number` once
 * `JSON.parse` has had them, and 0.1 + 0.2 is a poor reason to tell somebody their ledger is wrong.
 */
export function reconcile(
  bill: Bill,
  receivables: Receivables,
  trialBalance: TrialBalance,
): Reconciliation {
  // The account's own row, or nothing owed at all if the ledger has no receivables line for it.
  const row = receivables.rows.at(0);

  const checks: ReconciliationCheck[] = [
    {
      id: 'charged',
      label: 'Charged',
      billing: bill.amountDue,
      finance: row?.charged ?? 0,
      agrees: centsEqual(bill.amountDue, row?.charged ?? 0),
      note: 'What the bill comes to, and what was debited to receivables when it was issued.',
    },
    {
      id: 'settled',
      label: 'Settled',
      billing: bill.amountPaid,
      finance: row?.settled ?? 0,
      agrees: centsEqual(bill.amountPaid, row?.settled ?? 0),
      note: 'What has been paid against the bill, and what was credited to receivables for it.',
    },
    {
      id: 'outstanding',
      label: 'Still owed',
      billing: bill.balance,
      finance: row?.outstanding ?? 0,
      agrees: centsEqual(bill.balance, row?.outstanding ?? 0),
      note: 'The bill’s balance, and the receivables control account’s position on this account.',
    },
  ];

  const ledgerBalances = trialBalance.isBalanced && centsEqual(trialBalance.difference, 0);

  return {
    checks,
    ledgerBalances,
    reconciles: ledgerBalances && checks.every((check) => check.agrees),
  };
}

/**
 * Money equality to the cent — the smallest unit anything in GridCore is stored to.
 *
 * Lives in `lib/money.ts` now, re-exported here so nothing that imported it from the walk had to
 * change. WP-2.10's balance arithmetic was the third caller for this kind of sum.
 */
export { centsEqual };
