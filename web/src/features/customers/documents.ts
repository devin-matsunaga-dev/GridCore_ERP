import type { AccountStatement, StatementEntryKind, StatementRange } from '@/api/customers';
import type { Bill } from '@/api/billing';
import type { StatusTone } from '@/components/ui/status';
import { toCents } from '@/lib/money';

/**
 * The documents tab's logic, with no DOM in sight.
 *
 * The claims on that screen a customer would dispute — that the statement adds up, which column a
 * line belongs in, which bills can be reprinted — are worked out here and tested without rendering
 * anything. The same call `customer-360.ts`, `deposits.ts` and `notes.ts` already made.
 *
 * Money is compared in CENTS. These are `decimal` on the wire and `number` the moment `JSON.parse`
 * has had them, and 0.1 + 0.2 is a poor reason to tell a rep their statement does not balance.
 */

/** What each statement line reads as. */
const kindLabels: Record<StatementEntryKind, string> = {
  BillIssued: 'Bill issued',
  BillCorrected: 'Bill corrected',
  BillWithdrawn: 'Bill withdrawn',
  PaymentReceived: 'Payment received',
  DepositApplied: 'Deposit applied',
  DepositCollected: 'Deposit received',
  DepositRefunded: 'Deposit refunded',
};

/**
 * The tone each line carries.
 *
 * Money arriving is the good outcome; a charge is informational rather than bad, because a bill is
 * not a failure. A withdrawal is a warning: it is the line somebody looks for when a balance moved
 * without a payment behind it.
 */
const kindTones: Record<StatementEntryKind, StatusTone> = {
  BillIssued: 'info',
  BillCorrected: 'neutral',
  BillWithdrawn: 'warning',
  PaymentReceived: 'success',
  DepositApplied: 'info',
  DepositCollected: 'success',
  DepositRefunded: 'neutral',
};

/** What a statement line's kind reads as. */
export function statementKindLabel(kind: StatementEntryKind): string {
  return kindLabels[kind];
}

/** The pill tone a statement line renders with. */
export function statementKindTone(kind: StatementEntryKind): StatusTone {
  return kindTones[kind];
}

/**
 * Whether the statement's own arithmetic holds: opening plus every line equals closing.
 *
 * The host refuses to compose a statement that does not (`AccountStatement.Compose`), so this can
 * only fail if the two disagree about what a line means — which is exactly the kind of drift worth
 * catching on the screen rather than in a customer's hands. The tab renders a warning rather than
 * the figures when it comes back false.
 */
export function statementProvesOut(statement: AccountStatement): boolean {
  const moved = statement.entries.reduce((total, entry) => total + toCents(entry.amount), 0);

  return toCents(statement.openingBalance) + moved === toCents(statement.closingBalance);
}

/** Whether any line on the statement touched the deposit, so the column is worth showing at all. */
export function statementTouchesDeposit(statement: AccountStatement): boolean {
  return (
    toCents(statement.openingDepositHeld) !== 0
    || statement.entries.some((entry) => toCents(entry.depositAmount) !== 0)
  );
}

/**
 * The bills a rep can reprint, newest first.
 *
 * **Drafts are absent**, because a draft is not a document anybody was sent — the host answers a
 * 409 for one, and a list offering it is a list whose choices produce errors. The same call the
 * deposit tab makes about which bills a deposit could settle.
 */
export function billsThatCanBeReprinted(bills: readonly Bill[]): Bill[] {
  return bills
    .filter((bill) => bill.status !== 'Draft')
    .toSorted((left, right) => (right.issuedOn ?? '').localeCompare(left.issuedOn ?? ''));
}

/**
 * The range a freshly opened tab asks for: the last ninety days, ending today.
 *
 * The host's default when a caller names no range, restated here so the two date selects open on
 * the range the screen is about to show rather than empty. `today` is passed in rather than read
 * off the clock, so this stays pure.
 */
export function defaultStatementRange(today: Date): StatementRange {
  const from = new Date(today);

  from.setUTCDate(from.getUTCDate() - 90);

  return { from: isoDate(from), to: isoDate(today) };
}

/** Whether a range is one the host will accept — the check the two selects run before asking. */
export function isStatementRangeValid(range: StatementRange): boolean {
  return range.from.length > 0 && range.to.length > 0 && range.from <= range.to;
}

/** A date as the host's `DateOnly` wants it. */
export function isoDate(value: Date): string {
  return value.toISOString().slice(0, 10);
}

/**
 * What the browser saves a payment-history export as.
 *
 * The same rule the host applies to its `Content-Disposition` name — a fetched file has no name of
 * its own, because the header does not survive `Response.text()`. Kept in step by
 * `PaymentHistoryCsvTests` asserting the host half and `documents.test.ts` this one.
 */
export function paymentHistoryFileName(accountNumber: string, producedOn: Date): string {
  const safe = [...(accountNumber || '')]
    .map((character) => (/[A-Za-z0-9-]/.test(character) ? character : '-'))
    .join('');

  return `payment-history-${safe || 'account'}-${isoDate(producedOn)}.csv`;
}

/**
 * Hands the browser a CSV to save.
 *
 * **The `\uFEFF` is load-bearing.** The host serves the file with a UTF-8 byte-order mark, and
 * `Response.text()` strips it as it decodes — so writing the text straight back out would produce a
 * file that a spreadsheet on a clerk's desk opens with every accented place name mangled. Putting
 * one back is the whole reason this is not two lines at the call site.
 *
 * Returns whether it could: a browser without `createObjectURL` (a test environment, mostly) gets a
 * false rather than an exception, and the caller says so with a toast.
 */
export function downloadCsv(fileName: string, csv: string): boolean {
  if (typeof URL.createObjectURL !== 'function') return false;

  const url = URL.createObjectURL(new Blob(['\uFEFF', csv], { type: 'text/csv;charset=utf-8' }));
  const link = document.createElement('a');

  link.href = url;
  link.download = fileName;
  link.style.display = 'none';

  document.body.append(link);
  link.click();
  link.remove();

  // Freed on the next turn rather than immediately: a click on a freshly revoked URL is a download
  // that silently does nothing in some browsers.
  setTimeout(() => URL.revokeObjectURL(url), 0);

  return true;
}
