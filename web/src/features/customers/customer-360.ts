import type { Bill } from '@/api/billing';
import type { ServiceAccount, ServiceAccountHistoryEntry } from '@/api/customers';
import type { Payment, PaymentStatus } from '@/api/payments';
import { toneFor, type StatusTone } from '@/components/ui/status';
import { formatDate, formatLabel, formatMoney, formatQuantity } from '@/lib/format';
import { fromCents, toCents } from '@/lib/money';

/**
 * The 360° page's logic: what a customer owes, and everything that has happened to them as one
 * feed.
 *
 * Pure, and deliberately so — no React, no DOM, no network. The merge and the arithmetic are the
 * two things on this page a rep would dispute, so they are testable exhaustively in milliseconds
 * (CONVENTIONS.md ⚡), the same call `revenue-cycle.ts` and `registration.ts` made. The panels
 * render what this returns and work nothing out for themselves.
 *
 * Nothing here joins modules either. Every input arrived from the service that owns it — accounts
 * and their transitions from Customers, bills and their corrections from Billing, payments from
 * Payments — and this only puts them in order.
 */

/**
 * The kinds of thing that reach the feed, in CAUSAL order: an account is opened before it is
 * billed, a bill is corrected before it is paid.
 *
 * The order is load-bearing. It breaks ties between entries that happened at the same instant, so
 * **reordering this list reorders every same-instant cluster on the page** — the same warning
 * `CustomerMatchKind` carries about search precedence, and for the same reason.
 */
export const timelineKinds = ['account', 'bill', 'adjustment', 'payment'] as const;
export type TimelineKind = (typeof timelineKinds)[number];

/**
 * How precisely the source records when something happened.
 *
 * Not decoration. A bill is issued on a `DateOnly` — the accounting date, which is all Billing
 * stores — while a payment, a correction and an account transition all carry a full instant. The
 * feed has to sort them together, so a date becomes midnight UTC for ordering; rendering it as
 * "12:00 AM" would be inventing a time the utility never recorded, so the panel is told not to.
 */
export type TimelinePrecision = 'date' | 'time';

export type CustomerTimelineEntry = {
  /** Prefixed by kind, so two modules' ids can never collide as React keys. */
  id: string;
  kind: TimelineKind;
  title: string;
  detail: string | null;
  actor: string | null;
  occurredAt: string;
  precision: TimelinePrecision;
  tone: StatusTone;
};

/** What the timeline is built from — one collection per module, already fetched by its own panel. */
export type CustomerTimelineSources = {
  accounts: readonly ServiceAccount[];
  /** Keyed by account id. A missing key is a history panel still loading, not an account with none. */
  historyByAccountId: ReadonlyMap<string, readonly ServiceAccountHistoryEntry[]>;
  bills: readonly Bill[];
  payments: readonly Payment[];
};

/**
 * Every source merged into one reverse-chronological feed.
 *
 * The order is made TOTAL — instant, then causal kind, then id — rather than left to the sort's
 * stability. Two bills issued on the same day carry the same instant to the millisecond, and an
 * order that depended on which array they arrived in would reshuffle itself between renders.
 */
export function buildCustomerTimeline(sources: CustomerTimelineSources): CustomerTimelineEntry[] {
  return [
    ...accountEntries(sources.accounts, sources.historyByAccountId),
    ...billEntries(sources.bills),
    ...adjustmentEntries(sources.bills),
    ...paymentEntries(sources.payments),
  ].toSorted(newestFirst);
}

/**
 * Newest first, and total.
 *
 * Equal instants fall back to the causal order of the kinds — reversed, because the feed runs
 * backwards, so the payment sits above the bill it settled rather than below it. Equal there too
 * falls back to the id, which is a Guid v7 and so orders by creation.
 */
function newestFirst(left: CustomerTimelineEntry, right: CustomerTimelineEntry): number {
  const byInstant = Date.parse(right.occurredAt) - Date.parse(left.occurredAt);
  if (byInstant !== 0) return byInstant;

  const byKind = timelineKinds.indexOf(right.kind) - timelineKinds.indexOf(left.kind);
  if (byKind !== 0) return byKind;

  // Ordinal, never `localeCompare`: these are ids, not words, and a locale-aware order is neither
  // stable across browsers nor meaningful over hex.
  if (left.id === right.id) return 0;
  return left.id > right.id ? -1 : 1;
}

/** Account opened, and every transition since. The service record, not the audit trail. */
function accountEntries(
  accounts: readonly ServiceAccount[],
  historyByAccountId: ReadonlyMap<string, readonly ServiceAccountHistoryEntry[]>,
): CustomerTimelineEntry[] {
  return accounts.flatMap((account) =>
    (historyByAccountId.get(account.id) ?? []).map((entry) => ({
      id: `account:${entry.id}`,
      kind: 'account' as const,
      title: entry.fromStatus
        ? `Account ${account.accountNumber}: ${formatLabel(entry.fromStatus)} → ${formatLabel(entry.toStatus)}`
        : `Account ${account.accountNumber} opened`,
      detail: entry.reason,
      actor: entry.actorName ?? entry.actorId,
      occurredAt: entry.recordedAt,
      precision: 'time' as const,
      tone: toneFor(entry.toStatus),
    })),
  );
}

/**
 * A bill becomes an event when it is ISSUED, which is the act that makes it money somebody owes.
 * A draft is owed by nobody and publishes nothing, so it is not something that happened to this
 * customer; `issuedOn` being set is exactly the test for that.
 *
 * A bill issued and later cancelled keeps its entry, because it was issued on that day and the
 * customer was told so. The cancellation itself is not an entry: Billing records no instant for
 * it, and a feed cannot place an event it has no time for.
 *
 * The tone is the ISSUING's, not the bill's status today. This is a record of what happened; where
 * the bill stands now is the bills panel's job, and it carries the pill that says so.
 */
function billEntries(bills: readonly Bill[]): CustomerTimelineEntry[] {
  return bills
    .filter((bill) => bill.issuedOn !== null)
    .map((bill) => ({
      id: `bill:${bill.id}`,
      kind: 'bill' as const,
      title: `Bill ${bill.billNumber} issued — ${formatMoney(bill.totalAmount)}`,
      detail: [
        `${formatQuantity(bill.consumption)} ${bill.unitOfMeasure} on account ${bill.accountNumber}`,
        bill.dueDate ? `due ${formatDate(bill.dueDate)}` : null,
      ]
        .filter((part): part is string => part !== null)
        .join(' · '),
      actor: bill.actorName ?? bill.actorId,
      occurredAt: bill.issuedOn!,
      precision: 'date' as const,
      tone: toneFor('Issued'),
    }));
}

/**
 * WP-2.4's immutable corrections, each an event of its own.
 *
 * Only present when the bills were asked for with `includeAdjustments` — a list row carries the
 * running `adjustmentTotal` but no entries, and a total has no date to sort by. A bill fetched
 * without them contributes nothing here rather than one guessed entry.
 */
function adjustmentEntries(bills: readonly Bill[]): CustomerTimelineEntry[] {
  return bills.flatMap((bill) =>
    bill.adjustments.map((adjustment) => ({
      id: `adjustment:${adjustment.id}`,
      kind: 'adjustment' as const,
      title: `Bill ${bill.billNumber} ${adjustment.kind === 'Credit' ? 'credited' : 'charged'} ${formatMoney(Math.abs(adjustment.amount))}`,
      detail: adjustment.reason,
      actor: adjustment.actorName ?? adjustment.actorId,
      occurredAt: adjustment.recordedAt,
      precision: 'time' as const,
      tone: toneFor('Adjustment'),
    })),
  );
}

/** What each outcome reads as in the feed. A refusal is an event too — it is why money is still owed. */
const paymentVerbs: Record<PaymentStatus, string> = {
  Pending: 'pending',
  Approved: 'taken',
  Declined: 'declined',
  Failed: 'failed',
  Refunded: 'refunded',
};

/**
 * Every attempt, settled or not. A decline is the answer to "why does this customer still owe
 * money", so filtering the feed down to the money that moved would hide the useful half.
 *
 * Timed at settlement where there is one and at the request otherwise — a declined attempt never
 * settles, and dropping it off the feed for want of a `settledAt` is how it would go missing.
 */
function paymentEntries(payments: readonly Payment[]): CustomerTimelineEntry[] {
  return payments.map((payment) => ({
    id: `payment:${payment.id}`,
    kind: 'payment' as const,
    title: `Payment ${paymentVerbs[payment.status]} — ${formatMoney(payment.amount)}`,
    detail: [
      `${payment.paymentNumber} against bill ${payment.billNumber}`,
      payment.providerMessage,
    ]
      .filter((part): part is string => Boolean(part))
      .join(' · '),
    actor: payment.actorName ?? payment.actorId,
    occurredAt: payment.settledAt ?? payment.requestedAt,
    precision: 'time' as const,
    tone: toneFor(payment.status),
  }));
}

/** What a customer owes, and the parts it is made of. */
export type CustomerBalance = {
  /** What the rate engine printed on every bill that reached the customer. Never moves again. */
  billed: number;
  /** The signed sum of corrections since — negative when credits outweigh charges. */
  adjustments: number;
  /** `billed + adjustments`: what the customer has actually been asked for, corrections included. */
  netBilled: number;
  /** What Billing has recorded as settled against those bills. */
  paid: number;
  /** `billed + adjustments - paid`. Positive is owed to the utility. */
  outstanding: number;
  /** How many of those bills still have something owing on them. */
  outstandingBills: number;
  /** What is owed on bills the overdue review has already moved. */
  overdue: number;
};

/**
 * Whether a bill is part of what this customer owes.
 *
 * Issued and not cancelled. A draft was never sent and is owed by nobody — it is a row a billing
 * run produced, and every walk of the demonstration screen leaves more of them behind. A cancelled
 * bill was withdrawn, and counting either would put money on a rep's screen that the customer has
 * never been asked for.
 */
export function countsTowardBalance(bill: Bill): boolean {
  return bill.issuedOn !== null && bill.status !== 'Cancelled';
}

/**
 * What the customer owes, from Billing's own figures.
 *
 * Summed in CENTS. A customer with a year of bills is a dozen additions of parsed decimals, and
 * floating-point residue is how a balance ends up reading $284.99999999999994.
 *
 * `paid` comes from the bills, not from the payments list, and the difference matters: a bill's
 * `amountPaid` is what Billing recorded when it consumed `PaymentApproved`, whereas the payments
 * register holds attempts — declines included, and approvals whose consumer has not run yet. The
 * two disagreeing is a real and temporary state (WP-2.7's payment step polls through exactly that
 * window), and the figure a rep quotes has to be the one the biller will agree with.
 */
export function customerBalance(bills: readonly Bill[]): CustomerBalance {
  const owed = bills.filter(countsTowardBalance);

  let billed = 0;
  let adjustments = 0;
  let paid = 0;
  let overdue = 0;
  let outstandingBills = 0;

  for (const bill of owed) {
    billed += toCents(bill.totalAmount);
    adjustments += toCents(bill.adjustmentTotal);
    paid += toCents(bill.amountPaid);

    if (toCents(bill.balance) !== 0) outstandingBills += 1;
    if (bill.status === 'Overdue') overdue += toCents(bill.balance);
  }

  return {
    billed: fromCents(billed),
    adjustments: fromCents(adjustments),
    netBilled: fromCents(billed + adjustments),
    paid: fromCents(paid),
    outstanding: fromCents(billed + adjustments - paid),
    outstandingBills,
    overdue: fromCents(overdue),
  };
}

/**
 * The last payment the utility actually holds, or nothing.
 *
 * Settled only, and this one IS filtered — "last payment" on a summary tile is a statement about
 * money received, and answering it with a decline from this morning would be a lie told in a
 * confident font. The declines are still on the feed, where they read as what they are.
 */
export function lastSettledPayment(payments: readonly Payment[]): Payment | undefined {
  return payments
    .filter((payment) => payment.isSettled && payment.settledAt !== null)
    .reduce<Payment | undefined>(
      (latest, payment) =>
        latest === undefined || Date.parse(payment.settledAt!) > Date.parse(latest.settledAt!)
          ? payment
          : latest,
      undefined,
    );
}

/**
 * Open accounts first, then by account number.
 *
 * A disconnected supply is what somebody rang up about; a closed account from four years ago is
 * history and belongs below it.
 */
export function sortAccounts(accounts: readonly ServiceAccount[]): ServiceAccount[] {
  return accounts.toSorted((left, right) => {
    const closed = Number(left.status === 'Closed') - Number(right.status === 'Closed');

    return closed || left.accountNumber.localeCompare(right.accountNumber, undefined, { numeric: true });
  });
}
