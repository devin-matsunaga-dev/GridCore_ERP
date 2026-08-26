import type {
  DepositAccountRequirement,
  DepositEntry,
  DepositEntryKind,
  DepositLedger,
} from '@/api/customers';
import type { Bill } from '@/api/billing';
import type { StatusTone } from '@/components/ui/status';
import { toCents } from '@/lib/money';

/**
 * The deposit tab's logic, with no DOM in sight.
 *
 * The claims on that screen a rep would dispute — how much is held, whether the customer is short
 * of what the schedule asks, and how much of the deposit a given bill could actually take — are
 * worked out here and tested without rendering anything. The same call `customer-360.ts`,
 * `contacts.ts` and `registration.ts` already made.
 *
 * Every amount is compared in CENTS. These are `decimal` on the wire and `number` the moment
 * `JSON.parse` has had them, and 0.1 + 0.2 is a poor reason to tell a rep a fully covered bill has
 * a cent left on it.
 */

/** What each movement reads as in a table row. */
const kindLabels: Record<DepositEntryKind, string> = {
  Collected: 'Collected',
  Applied: 'Applied to bill',
  Refunded: 'Refunded',
  Transferred: 'Carried on transfer',
};

/**
 * The tone each movement carries.
 *
 * Money coming in is the good outcome for a deposit's purpose, money going back out is neutral —
 * a refund is not a failure, it is the lifecycle finishing. An application is informational,
 * because what it says is that money moved between two places the utility already held it in.
 */
const kindTones: Record<DepositEntryKind, StatusTone> = {
  Collected: 'success',
  Applied: 'info',
  Refunded: 'neutral',

  // Neutral, because nothing moved. A carry on a transfer keeps a deposit exactly where it was —
  // colouring it as money in or money out would be the screen making the claim the ledger refuses to.
  Transferred: 'neutral',
};

/** What a movement's kind reads as. */
export function depositKindLabel(kind: DepositEntryKind): string {
  return kindLabels[kind];
}

/** The pill tone a movement renders with. */
export function depositKindTone(kind: DepositEntryKind): StatusTone {
  return kindTones[kind];
}

/** Where a customer's deposit stands against what the schedule asks of their class. */
export type DepositStanding = 'none' | 'short' | 'covered';

/**
 * Whether the utility is holding what it asked for.
 *
 * Three states rather than two, because "nothing held" and "not enough held" are different
 * conversations: the first is a deposit that was waived or never taken, and the second is a
 * part-payment at the counter with a balance still to come. A screen that showed both as "short"
 * would send a rep chasing a customer who was never asked.
 *
 * Measured against `requirement.requiredAmount` since WP-2.17 — the sum over the supplies this
 * customer actually takes, rather than one figure read off their class. A customer taking nothing is
 * covered: the schedule asks nothing of somebody with no open account.
 *
 * Compared in cents, so a balance of 75 against a requirement of 75 is covered rather than short by
 * a rounding artefact.
 */
export function depositStanding(ledger: DepositLedger): DepositStanding {
  const held = toCents(ledger.balance);

  if (held === 0) {
    return toCents(ledger.requirement.requiredAmount) === 0 ? 'covered' : 'none';
  }

  return held >= toCents(ledger.requirement.requiredAmount) ? 'covered' : 'short';
}

/**
 * How one account's deposit line reads, in the words a rep would use down the telephone.
 *
 * The distinction the two-part rule exists for: "two months of your average usage" and "the
 * published minimum" are different conversations that happen to produce a number each, and a screen
 * showing only the figure makes the first indefensible.
 */
export function depositBasisLabel(line: DepositAccountRequirement): string {
  if (!line.isMetered) return 'Flat charge — unmetered service';

  if (line.isUsageBased && line.averageMonthlyUsage !== null && line.usageMonths !== null) {
    return `${line.usageMonths} months of ${line.averageMonthlyUsage.toLocaleString()} a month`;
  }

  return line.hasUsageHistory ? 'Minimum — usage is below it' : 'Minimum — nothing read here yet';
}

/** What the standing reads as beside the balance. */
export function depositStandingLabel(standing: DepositStanding): string {
  switch (standing) {
    case 'none':
      return 'None held';
    case 'short':
      return 'Below the schedule';
    default:
      return 'Schedule met';
  }
}

/** The tone the standing renders with. A shortfall is a warning, never an error — it is ordinary. */
export function depositStandingTone(standing: DepositStanding): StatusTone {
  switch (standing) {
    case 'none':
      return 'neutral';
    case 'short':
      return 'warning';
    default:
      return 'success';
  }
}

/**
 * The bills a held deposit could be put against, newest first.
 *
 * Outstanding bills only, and only this customer's — the host refuses anything else, and a select
 * offering a draft or a settled bill would be a select whose choices produce 409s. A bill is
 * outstanding when it has been issued, has not been cancelled and still has a balance; that is the
 * same rule `countsTowardBalance` applies for the summary, plus the balance itself.
 */
export function billsADepositCouldSettle(bills: readonly Bill[]): Bill[] {
  return bills
    .filter((bill) => bill.issuedOn !== null && bill.status !== 'Cancelled' && toCents(bill.balance) > 0)
    .toSorted((left, right) => (left.id > right.id ? -1 : left.id < right.id ? 1 : 0));
}

/**
 * The most of a deposit that may go against one bill: whichever of the two runs out first.
 *
 * Both ceilings are real and the host enforces both — a deposit cannot go below zero, and a bill
 * cannot take more than it is owed. Working out the smaller one here is what lets the form show a
 * rep the number before they type over it, rather than answering with a 409 afterwards.
 */
export function applicableAmount(heldBalance: number, billBalance: number): number {
  return Math.min(toCents(heldBalance), toCents(billBalance)) / 100;
}

/**
 * The movements a table renders, newest first.
 *
 * The host already returns them that way — ids are Guid v7, so its key order is chronological — and
 * this makes the order the screen's own rather than something it inherits and cannot state. Total,
 * and not left to sort stability: two movements recorded in the same millisecond fall back to the
 * id, which is the lesson `buildCustomerTimeline` carries.
 */
export function sortDepositEntries(entries: readonly DepositEntry[]): DepositEntry[] {
  return entries.toSorted((left, right) => {
    const byInstant = Date.parse(right.recordedAt) - Date.parse(left.recordedAt);
    if (byInstant !== 0) return byInstant;

    // Ordinal, never `localeCompare`: these are ids, not words.
    if (left.id === right.id) return 0;
    return left.id > right.id ? -1 : 1;
  });
}
