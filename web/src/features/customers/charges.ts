import type { AccountCharge, AccountChargeStatus, FeeCode, FeeScheduleEntry } from '@/api/billing';
import type { ServiceAccount } from '@/api/customers';
import type { StatusTone } from '@/components/ui/status';

/**
 * The charges tab's logic, with no DOM in sight — the same call `transitions.ts`, `deposits.ts` and
 * `applications.ts` all made.
 *
 * This is WP-2.16's screen, landing with WP-2.18. That package shipped the fee schedule, the charge
 * register and their endpoints with no UI at all, on the stated grounds that "the desk surface that
 * raises a fee belongs with the workflow that raises one" — and named WP-2.18, WP-2.21 and WP-2.22
 * as the candidates, with whichever went first owing the screen. WP-2.18 went first.
 */

const feeCodeLabels: Record<FeeCode, string> = {
  ServiceConnection: 'Service connection',
  Reconnection: 'Reconnection',
  ReturnedPayment: 'Returned payment',
  MeterTest: 'Meter test',
  Inspection: 'Inspection',
  UnauthorizedConnection: 'Unauthorised connection',
  LateCharge: 'Late payment charge',
};

/**
 * The tone each charge status renders with.
 *
 * Pending is money the utility is going to ask for and has not yet — a warning, the same tone an
 * unconnected account carries. Billed is the fee having reached a bill, which is what raising it
 * was for. Cancelled is neutral, like every other withdrawn thing on the site.
 */
const statusTones: Record<AccountChargeStatus, StatusTone> = {
  Pending: 'warning',
  Billed: 'success',
  Cancelled: 'neutral',
};

/** What a published fee reads as. Sentence case, as DESIGN.md asks. */
export function feeCodeLabel(code: FeeCode): string {
  return feeCodeLabels[code];
}

/** The pill tone a charge renders with. */
export function chargeStatusTone(status: AccountChargeStatus): StatusTone {
  return statusTones[status];
}

/**
 * The accounts a fee may be raised against, by account number.
 *
 * **Closed accounts included, deliberately.** The host does not refuse one, and it is right not to:
 * a meter-test fee or an unauthorised-connection penalty is often the last thing that happens to a
 * supply, after it has been closed. Filtering them out here would be a screen inventing a rule the
 * domain does not have — the opposite of `movableAccounts`, which hides closed accounts because the
 * host genuinely refuses to move out of one.
 */
export function chargeableAccounts(accounts: readonly ServiceAccount[]): ServiceAccount[] {
  return accounts.toSorted((left, right) => left.accountNumber.localeCompare(right.accountNumber));
}

/**
 * What the schedule says a fee costs today, or `undefined` when the catalogue publishes no figure
 * for it.
 *
 * The catalogue is fetched priced for a day, so this is a lookup rather than a calculation: the
 * arithmetic that chose between two effective-dated versions happened on the host, which is the only
 * place that may do it.
 */
export function priceOf(schedule: readonly FeeScheduleEntry[], code: FeeCode): FeeScheduleEntry | undefined {
  return schedule.find((entry) => entry.code === code);
}

/**
 * The published fees a rep may raise from this screen: the flat ones, and only the flat ones.
 *
 * **A rate fee is deliberately not offerable** (WP-2.19). The late charge is a percentage of a
 * past-due balance the register computes, so it has no figure until something is charged on it — and
 * a rep choosing it would have to supply the balance, which is the same thing as inventing one. The
 * late-charge run raises it; this desk never does. That is the same argument the tab already makes
 * for having no amount field, one level up.
 *
 * Driven off the schedule rather than off `feeCodes`, so a code the catalogue publishes no figure
 * for today is not offered either — the host would refuse it, and a select that offered it would be
 * a select that produces 400s.
 */
export function raisableFees(schedule: readonly FeeScheduleEntry[]): FeeScheduleEntry[] {
  return schedule.filter((entry) => entry.basis === 'Flat' && entry.amount !== null);
}

/**
 * What a charge's figure reads as beside its label: a flat fee is the amount, and a rate fee says
 * what it was taken on.
 *
 * The three columns a rate charge stamps — the schedule row, the rate and the basis — are what let a
 * clerk answer "why is this $2.35" years later without re-running an arrears query. This is where
 * two of them reach a screen.
 */
export function chargeBasisNote(charge: AccountCharge): string | undefined {
  if (charge.basis !== 'Rate' || charge.rate === null || charge.basisAmount === null) return undefined;

  return `${(charge.rate * 100).toFixed(2)}% of ${charge.basisAmount.toFixed(2)} past due`;
}

/**
 * Whether a charge can still be withdrawn or billed at the counter.
 *
 * Read off the host's own `isPending` rather than compared against a status string here: `Billed`
 * is terminal on the host, and a button offered against one would be a button that produces 409s.
 */
export function isActionable(charge: AccountCharge): boolean {
  return charge.isPending;
}

/** What a customer has been charged and not yet billed for — the figure the tab leads with. */
export function pendingTotal(charges: readonly AccountCharge[]): number {
  return charges.filter(isActionable).reduce((total, charge) => total + charge.amount, 0);
}

/**
 * The register, newest first.
 *
 * The host already returns them that way — ids are Guid v7, so its key order is chronological — and
 * this makes the order the screen's own rather than something it inherits and cannot state. Total,
 * and not left to sort stability: two charges raised in the same millisecond fall back to the id.
 */
export function sortCharges(charges: readonly AccountCharge[]): AccountCharge[] {
  return charges.toSorted((left, right) => {
    const byInstant = Date.parse(right.raisedAt) - Date.parse(left.raisedAt);
    if (byInstant !== 0) return byInstant;

    // Ordinal, never `localeCompare`: these are ids, not words.
    if (left.id === right.id) return 0;
    return left.id > right.id ? -1 : 1;
  });
}
