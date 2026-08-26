import type {
  AccountArrears,
  Delinquency,
  DisconnectionEligibility,
  DunningNotice,
  DunningNoticeType,
  DunningStep,
  ServiceAccount,
} from '@/api/customers';
import type { StatusTone } from '@/components/ui/status';

/**
 * The delinquency tab's logic, with no DOM in sight — the same call `charges.ts`, `transitions.ts`,
 * `deposits.ts` and `applications.ts` all made.
 *
 * WP-2.19. What is here is presentation: labels, tones, orderings and the two or three derived
 * figures a screen needs. **No rule is re-implemented.** Whether an account may be disconnected,
 * how much of a deposit qualifies against its arrears and which dunning step it has reached are all
 * decided by the host and arrive on the wire — a browser that computed any of them would be a
 * second opinion about whether somebody's electricity gets cut off.
 */

const noticeLabels: Record<DunningNoticeType, string> = {
  Reminder: 'Payment reminder',
  Delinquency: 'Notice of delinquency',
  Disconnection: 'Notice of disconnection',
};

/**
 * The tone each notice renders with.
 *
 * A reminder is informational, a delinquency notice is a warning, and a disconnection notice is the
 * one that carries the utility's most serious intention — the same three-step escalation the
 * semantic map already uses for a bill going from issued to overdue.
 */
const noticeTones: Record<DunningNoticeType, StatusTone> = {
  Reminder: 'info',
  Delinquency: 'warning',
  Disconnection: 'danger',
};

/** What a served notice reads as. Sentence case, as DESIGN.md asks. */
export function noticeLabel(noticeType: DunningNoticeType): string {
  return noticeLabels[noticeType];
}

/** The pill tone a notice renders with. */
export function noticeTone(noticeType: DunningNoticeType): StatusTone {
  return noticeTones[noticeType];
}

/**
 * The accounts a delinquency picture can be read for, by account number.
 *
 * **Closed accounts included, deliberately.** A closed account can still hold an overdue bill — the
 * comment `BillStatus` makes about exactly that — and chasing it is what a debt-collection desk
 * does. Filtering them out would be a screen inventing a rule the host does not have.
 */
export function delinquencyAccounts(accounts: readonly ServiceAccount[]): ServiceAccount[] {
  return accounts.toSorted((left, right) => left.accountNumber.localeCompare(right.accountNumber));
}

/**
 * The ageing bands with anything in them, in the order the host published them.
 *
 * The host always answers with every band, at nothing where nothing is owed in it — which is right
 * for a report and wrong for a summary strip, where five zeroes say less than one figure. The order
 * is the host's and is never re-sorted here: it is the ageing, and an ageing read out of order is a
 * different report.
 */
export function occupiedBuckets(arrears: AccountArrears): AccountArrears['buckets'] {
  return arrears.buckets.filter((bucket) => bucket.amount !== 0);
}

/**
 * The notices served, newest first.
 *
 * The host already returns them that way — ids are Guid v7, so its key order is chronological — and
 * this makes the order the screen's own rather than something it inherits and cannot state. By the
 * day served rather than the day recorded, because that is the date the statutory clock runs from
 * and the one a rep reads out.
 */
export function sortNotices(notices: readonly DunningNotice[]): DunningNotice[] {
  return notices.toSorted((left, right) => {
    if (left.servedOn !== right.servedOn) return left.servedOn < right.servedOn ? 1 : -1;

    // Ordinal, never `localeCompare`: these are ids, not words.
    if (left.id === right.id) return 0;
    return left.id > right.id ? -1 : 1;
  });
}

/**
 * The step a rep would serve next, or `undefined` where there is nothing to serve.
 *
 * The host says which step the account has *reached* (`dueStep`); this says whether it has already
 * been served, which is the difference between a queue and a to-do list. A step served for an
 * earlier, smaller arrears is deliberately treated as served — re-serving a notice because the debt
 * grew would restart a statutory clock the customer is already inside.
 */
export function nextNoticeToServe(picture: Delinquency): DunningStep | undefined {
  if (picture.dueStep === null) return undefined;

  const served = new Set(picture.notices.map((notice) => notice.noticeType));

  return served.has(picture.dueStep.noticeType) ? undefined : picture.dueStep;
}

/**
 * What the eligibility strip leads with: one sentence saying where the account stands.
 *
 * **The deposit case is called out by name**, because it is the one the statute exists for and the
 * one a rep has to be able to explain: the customer is not being cut off, and the reason is that
 * their own money cleared the debt.
 */
export function eligibilitySummary(eligibility: DisconnectionEligibility): string {
  if (eligibility.depositClearsArrears) {
    return 'The security deposit clears the arrears, so this account is not eligible for disconnection.';
  }

  if (eligibility.isEligible) {
    return 'Every test is satisfied: this account is eligible for disconnection for non-payment.';
  }

  if (eligibility.blockers.length === 1) {
    return `Not eligible for disconnection — ${eligibility.blockers[0].toLowerCase()} is outstanding.`;
  }

  return `Not eligible for disconnection: ${eligibility.blockers.length} of the four tests are outstanding.`;
}

/** The tone the eligibility strip renders with. Eligible is the serious one, not the successful one. */
export function eligibilityTone(eligibility: DisconnectionEligibility): StatusTone {
  return eligibility.isEligible ? 'danger' : 'neutral';
}

/**
 * Whether there is a deposit for an evaluation to actually move.
 *
 * The evaluation is worth running either way — it is what WP-2.21's disconnection consumes, and it
 * writes the audit entry that says why — but a button that promises to apply a deposit when there is
 * none to apply reads as broken.
 */
export function hasOffsetToApply(eligibility: DisconnectionEligibility): boolean {
  return eligibility.offsetAmount > 0 && !eligibility.isOffsetApplied;
}
