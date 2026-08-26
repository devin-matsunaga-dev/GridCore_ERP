import type {
  AccountTransition,
  AccountTransitionKind,
  CustomerClass,
  CustomerStatus,
  ServiceAccount,
  TransitionReasonCode,
} from '@/api/customers';
import type { StatusTone } from '@/components/ui/status';

/**
 * The transitions tab's logic, with no DOM in sight.
 *
 * The claims on that screen a rep would dispute — which reasons a given move may be recorded under,
 * which of their accounts can still be moved out of, and what a row on the register actually says —
 * are worked out here and tested without rendering anything. The same call `deposits.ts`,
 * `notes.ts` and `customer-360.ts` already made.
 *
 * **The reason map is a MIRROR of `TransitionReasons` on the host, and the host is the authority.**
 * A select that offered a code the host refuses would be a select whose choices produce 400s, and
 * one that hid a code the host allows would quietly take an option away from a desk. The two are
 * kept in step by a test on this side and `TransitionReasonTests` on that one; whoever adds a
 * reason code changes both.
 */

const kindLabels: Record<AccountTransitionKind, string> = {
  ClassChanged: 'Class changed',
  StatusChanged: 'Status changed',
  MovedIn: 'Moved in',
  MovedOut: 'Moved out',
  Transferred: 'Transferred',
};

/**
 * The tone each kind renders with.
 *
 * A move-in and a transfer are the utility gaining or keeping a customer, so they read as success; a
 * move-out is the end of a supply and reads as a warning rather than an error, because it is
 * ordinary. The two record changes are informational — nothing about the supply moved.
 */
const kindTones: Record<AccountTransitionKind, StatusTone> = {
  ClassChanged: 'info',
  StatusChanged: 'info',
  MovedIn: 'success',
  MovedOut: 'warning',
  Transferred: 'success',
};

const reasonLabels: Record<TransitionReasonCode, string> = {
  Other: 'Other (say what happened)',
  CustomerRequest: 'Customer request',
  PremiseNowTrading: 'Premise now trading',
  PremiseNowResidential: 'Premise now residential',
  MisclassifiedAtIntake: 'Misclassified at intake',
  UnpaidBalance: 'Unpaid balance',
  BalanceSettled: 'Balance settled',
  IdentityDisputed: 'Identity disputed',
  Deceased: 'Deceased',
  NewOccupancy: 'New occupancy',
  EndOfTenancy: 'End of tenancy',
  PropertyVacated: 'Property vacated',
  PropertyDemolished: 'Property demolished',
  Relocation: 'Relocation',
};

/**
 * Which reason codes fit which kind — the mirror of `TransitionReasons.For` on the host.
 *
 * `Other` is last in every list, because it is the one a rep should reach for when none of the
 * others fits rather than the one their eye lands on first.
 */
const reasonsByKind: Record<AccountTransitionKind, readonly TransitionReasonCode[]> = {
  // No CustomerRequest: a class is what the premise is used for, not what its occupant would prefer
  // to be billed as.
  ClassChanged: ['PremiseNowTrading', 'PremiseNowResidential', 'MisclassifiedAtIntake', 'Other'],
  StatusChanged: ['CustomerRequest', 'UnpaidBalance', 'BalanceSettled', 'IdentityDisputed', 'Deceased', 'Other'],
  MovedIn: ['NewOccupancy', 'Relocation', 'CustomerRequest', 'Other'],
  MovedOut: ['EndOfTenancy', 'PropertyVacated', 'PropertyDemolished', 'Deceased', 'CustomerRequest', 'Other'],
  // Short on purpose: the codes that end a supply for good describe a move-OUT, and offering them
  // here would let a rep record a customer as having left while opening them an account elsewhere.
  Transferred: ['Relocation', 'CustomerRequest', 'Other'],
};

/** What a transition's kind reads as. */
export function transitionKindLabel(kind: AccountTransitionKind): string {
  return kindLabels[kind];
}

/** The pill tone a transition renders with. */
export function transitionKindTone(kind: AccountTransitionKind): StatusTone {
  return kindTones[kind];
}

/** What a reason code reads as in a select and on a row. */
export function transitionReasonLabel(code: TransitionReasonCode): string {
  return reasonLabels[code];
}

/** The reason codes a transition of `kind` may be recorded under, in the order they read. */
export function transitionReasonsFor(kind: AccountTransitionKind): readonly TransitionReasonCode[] {
  return reasonsByKind[kind];
}

/**
 * Whether `code` obliges the operator to write something as well.
 *
 * True for `Other` and nothing else, exactly as on the host. Asking here as well is not
 * belt-and-braces: it is the difference between a form that says so before the rep presses save and
 * one that answers with a 400 afterwards.
 */
export function transitionNeedsNotes(code: TransitionReasonCode): boolean {
  return code === 'Other';
}

/**
 * What one row of the register says, in the customer's terms.
 *
 * `fromValue` and `toValue` mean different things per kind — a class name, a status name, an account
 * number — so reading them is this function's job and not the table's. A move-in has no before and a
 * move-out has no after, which is what the two one-sided branches are.
 */
export function describeTransition(transition: AccountTransition): string {
  const from = transition.fromValue;
  const to = transition.toValue;

  switch (transition.kind) {
    case 'ClassChanged':
    case 'StatusChanged':
      return from && to ? `${from} → ${to}` : (to ?? from ?? '—');
    case 'MovedIn':
      return to ? `Service opened on ${to}` : 'Service opened';
    case 'MovedOut':
      return from ? `Service closed on ${from}` : 'Service closed';
    default:
      return from && to ? `${from} → ${to}` : 'Service transferred';
  }
}

/**
 * The accounts a customer can still be moved out of, or transferred from.
 *
 * Only the ones still holding their premise: a closed account has already been moved out of, and
 * offering it would be a select whose choices produce 409s — the call `billsADepositCouldSettle`
 * makes about a settled bill. Ordered by account number, which is what a rep reads them by.
 */
export function movableAccounts(accounts: readonly ServiceAccount[]): ServiceAccount[] {
  return accounts
    .filter((account) => account.status !== 'Closed')
    .toSorted((left, right) => left.accountNumber.localeCompare(right.accountNumber));
}

/**
 * The class a customer would be moved to — the other one.
 *
 * There are two, so a class change is a button rather than a select, and the host refuses a move to
 * the class already held with a 409. Working the target out here is what lets the button say
 * "Change to commercial" instead of asking a rep to pick from a list of one useful option.
 */
export function otherClass(current: CustomerClass): CustomerClass {
  return current === 'Residential' ? 'Commercial' : 'Residential';
}

/**
 * The register, newest first.
 *
 * The host already returns them that way — ids are Guid v7, so its key order is chronological — and
 * this makes the order the screen's own rather than something it inherits and cannot state. Total,
 * and not left to sort stability: two transitions recorded in the same millisecond fall back to the
 * id, which is the lesson `buildCustomerTimeline` carries.
 *
 * Sorted on `recordedAt` and NOT on `effectiveOn`. The register is a record of what was done and
 * when it was done; a list ordered by effective date would put a change dated next month above one
 * made this morning, and a rep reading down it would not be able to tell what happened last.
 */
export function sortTransitions(transitions: readonly AccountTransition[]): AccountTransition[] {
  return transitions.toSorted((left, right) => {
    const byInstant = Date.parse(right.recordedAt) - Date.parse(left.recordedAt);
    if (byInstant !== 0) return byInstant;

    // Ordinal, never `localeCompare`: these are ids, not words.
    if (left.id === right.id) return 0;
    return left.id > right.id ? -1 : 1;
  });
}

/**
 * Whether a transition was dated for a day other than the one it was recorded on.
 *
 * What the tab marks, because it is the fact a back-dated re-classification hides behind: the row
 * says "today" in the recorded column and prices from last month. Compared as calendar days in the
 * viewer's own zone, which is how both are rendered.
 */
export function isDated(transition: AccountTransition): boolean {
  const recordedOn = new Date(transition.recordedAt);
  const [year, month, day] = transition.effectiveOn.split('-').map(Number);

  return (
    recordedOn.getFullYear() !== year
    || recordedOn.getMonth() + 1 !== month
    || recordedOn.getDate() !== day
  );
}

/** The statuses a customer may move to, as the host reports them on the record. */
export function allowedStatuses(allowedTransitions: readonly string[]): CustomerStatus[] {
  return allowedTransitions as CustomerStatus[];
}
