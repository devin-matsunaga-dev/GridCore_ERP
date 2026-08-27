import type {
  ArrangementInstalment,
  ArrangementLimit,
  CustomerClass,
  PaymentArrangement,
  PaymentArrangementStatus,
  ServiceAccount,
} from '@/api/customers';
import type { StatusTone } from '@/components/ui/status';

/**
 * The arrangements tab's logic, with no DOM in sight — the same call `delinquency.ts`,
 * `charges.ts`, `transitions.ts`, `deposits.ts` and `applications.ts` all made.
 *
 * WP-2.20. What is here is presentation: labels, tones, orderings and the two or three derived
 * figures a screen needs. **No rule is re-implemented.** Whether an arrangement protects an account,
 * whether it needs approving, and what the schedule adds up to are all decided by the host and
 * arrive on the wire — a browser that computed any of them would be a second opinion about whether
 * somebody's electricity gets cut off.
 *
 * The one arithmetic here is `previewSchedule`, and it is explicitly a *preview*: it exists so a rep
 * can read the instalments back to a customer before committing, and what is committed is whatever
 * the host schedules. See its own note.
 */

const statusTones: Record<PaymentArrangementStatus, StatusTone> = {
  // Offered and not in force: informational, because nothing has been agreed.
  Proposed: 'info',

  // In force. Success, and not "warning": the customer is paying, which is the outcome the whole
  // feature exists to produce.
  Active: 'success',

  // Paid off. Neutral rather than success — it is history, and a rep scanning a list needs the
  // active one to stand out from the ones that are done.
  Kept: 'neutral',

  // The case disconnection exists for.
  Broken: 'danger',
};

const statusLabels: Record<PaymentArrangementStatus, string> = {
  Proposed: 'Proposed',
  Active: 'Active',
  Kept: 'Kept',
  Broken: 'Broken',
};

/** The pill tone an arrangement renders with. */
export function arrangementTone(status: PaymentArrangementStatus): StatusTone {
  return statusTones[status];
}

/** What a standing reads as. Sentence case, as DESIGN.md asks. */
export function arrangementLabel(status: PaymentArrangementStatus): string {
  return statusLabels[status];
}

/**
 * The accounts an arrangement can be made against, by account number.
 *
 * **Closed accounts included, deliberately** — the call `delinquencyAccounts` made. A closed account
 * can still hold an overdue bill, and arranging payment of it is exactly what a debt-collection desk
 * does.
 */
export function arrangeableAccounts(accounts: readonly ServiceAccount[]): ServiceAccount[] {
  return accounts.toSorted((left, right) => left.accountNumber.localeCompare(right.accountNumber));
}

/**
 * The arrangements newest first.
 *
 * The host already returns them that way — ids are Guid v7, so its key order is chronological — and
 * this makes the order the screen's own rather than something it inherits and cannot state.
 */
export function sortArrangements(arrangements: readonly PaymentArrangement[]): PaymentArrangement[] {
  return arrangements.toSorted((left, right) => {
    // Ordinal, never `localeCompare`: these are ids, not words.
    if (left.id === right.id) return 0;
    return left.id > right.id ? -1 : 1;
  });
}

/**
 * The arrangement standing against the account, or `undefined` where none is.
 *
 * **Read off `standing`, never off `status`.** An arrangement that missed an instalment yesterday is
 * still `Active` in the column until the review run comes round, and the host says so — but its
 * `standing` is already `Broken`, and that is what the account's protection turns on.
 */
export function standingArrangement(
  arrangements: readonly PaymentArrangement[],
): PaymentArrangement | undefined {
  return sortArrangements(arrangements).find(
    (arrangement) => arrangement.standing === 'Proposed' || arrangement.standing === 'Active',
  );
}

/** Whether a fresh arrangement may be offered — nothing is standing against the account. */
export function canPropose(arrangements: readonly PaymentArrangement[]): boolean {
  return standingArrangement(arrangements) === undefined;
}

/**
 * Whether this arrangement is waiting on a decision somebody else has to make.
 *
 * The host refuses activation while it is, so the button that would fail is disabled rather than
 * offered and then apologised for.
 */
export function awaitsApproval(arrangement: PaymentArrangement): boolean {
  return arrangement.status === 'Proposed' && arrangement.requiresApproval;
}

/** The instalment a payment would land on next, or `undefined` where the schedule is finished. */
export function nextInstalment(arrangement: PaymentArrangement): ArrangementInstalment | undefined {
  return arrangement.instalments.find((instalment) => !instalment.isSettled);
}

/**
 * Whether an instalment was missed as at `asOf`.
 *
 * The one date comparison this module makes, and it is a *display* decision: it puts the overdue
 * line in red. Whether the arrangement is broken because of it is the host's answer, on `standing`.
 */
export function isOverdue(instalment: ArrangementInstalment, asOf: string): boolean {
  return !instalment.isSettled && instalment.dueDate < asOf;
}

/** How far through the schedule the customer is, 0 to 1. */
export function progressOf(arrangement: PaymentArrangement): number {
  if (arrangement.scheduledAmount <= 0) return 0;

  return Math.min(1, arrangement.paidAmount / arrangement.scheduledAmount);
}

/** The ceiling governing `customerClass`, or `undefined` while the limits have not loaded. */
export function limitFor(
  limits: readonly ArrangementLimit[],
  customerClass: CustomerClass,
): ArrangementLimit | undefined {
  return limits.find((limit) => limit.customerClass === customerClass);
}

/**
 * Whether the figures a rep has typed would go beyond what they may agree alone.
 *
 * **A warning, not a rule.** The host decides, and it decides again with the ceilings as they stand
 * in the database; this exists so the form can say "this will need a supervisor" *before* the rep
 * reads a schedule out to a customer who then has to be rung back. A screen that got it wrong would
 * mislead a rep, and a screen that did not try would let them promise something they cannot deliver.
 */
export function willNeedApproval(
  limit: ArrangementLimit | undefined,
  balance: number,
  instalmentCount: number,
): boolean {
  if (limit === undefined) return false;

  return balance > limit.maximumBalance || instalmentCount > limit.maximumInstalments;
}

/** One line of a previewed schedule. */
export type SchedulePreviewLine = { sequence: number; amount: number; isDownPayment: boolean };

/**
 * What the schedule would look like, for reading back to a customer before anything is committed.
 *
 * **This mirrors `ArrangementSchedule.Build` and is not authoritative.** The host builds the real
 * schedule and refuses anything that does not add up; this is the same arithmetic in the browser so
 * a rep can say "three payments of $33.33 and a last one of $33.34" while the customer is still on
 * the telephone. Two implementations of one rule is a cost, and it is the same trade WP-2.15 made
 * when it mirrored the transition reasons — worth paying here because the alternative is a round
 * trip per keystroke, and cheap because the tests below hold the two to the same worked examples.
 *
 * Returns an empty list where the figures do not describe a schedule; the host's refusal is what
 * says why, in words.
 */
export function previewSchedule(
  balance: number,
  downPayment: number,
  instalmentCount: number,
): SchedulePreviewLine[] {
  if (!Number.isFinite(balance) || !Number.isFinite(downPayment)) return [];
  if (balance <= 0 || downPayment < 0 || downPayment >= balance) return [];
  if (!Number.isInteger(instalmentCount) || instalmentCount < 1) return [];

  const lines: SchedulePreviewLine[] = [];
  let sequence = 0;

  if (downPayment > 0) {
    lines.push({ sequence: ++sequence, amount: downPayment, isDownPayment: true });
  }

  // Worked in whole cents, never in floating-point dollars: 0.1 + 0.2 is not 0.3, and a schedule
  // that did not add up to its own total is exactly what this preview exists to avoid showing.
  const spreadCents = Math.round((balance - downPayment) * 100);
  const eachCents = Math.floor(spreadCents / instalmentCount);

  for (let index = 1; index <= instalmentCount; index += 1) {
    // THE REMAINDER LANDS ON THE LAST LINE, never spread — the host's rule, and the reason the
    // final figure is a subtraction rather than the computed one again.
    const cents = index === instalmentCount ? spreadCents - eachCents * (instalmentCount - 1) : eachCents;

    if (cents <= 0) return [];

    lines.push({ sequence: ++sequence, amount: cents / 100, isDownPayment: false });
  }

  return lines;
}
