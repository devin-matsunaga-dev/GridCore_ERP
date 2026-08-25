import type { CustomerMatchKind, CustomerSearchHit } from '@/api/customers';

/**
 * How a search result explains itself in the registry table (WP-2.9).
 *
 * Two functions and no rules. Classification, normalisation and ranking all happen on the host and
 * arrive already decided — a twin of any of them here would be a second implementation to keep in
 * step. That is the opposite call to the intake wizard's, and for the opposite reason: a form
 * refuses what a clerk typed and owes them an answer on the step that caused it, while a search
 * only asks.
 */

/** What the "matched on" cell reads for each way of matching. */
const matchLabels: Record<CustomerMatchKind, string> = {
  AccountNumber: 'Account number',
  MeterNumber: 'Meter number',
  Phone: 'Phone',
  Name: 'Name',
  Address: 'Service address',
};

/**
 * The label on a matched row: what matched, and whether the whole field did.
 *
 * "Exact" is worth saying out loud. A rep who quoted an account number wants to know the system
 * agreed it was that account rather than something merely containing those digits, and the
 * difference is the difference between reading the next row and stopping.
 */
export function matchLabel(hit: Pick<CustomerSearchHit, 'matchedOn' | 'isExact'>): string {
  const kind = matchLabels[hit.matchedOn];

  return hit.isExact ? `Exact ${kind.toLowerCase()}` : kind;
}

/**
 * The value beneath the label — what the row actually matched on, or the premise it came through
 * when the match itself is already the customer's name.
 *
 * A customer with two open accounts shows neither address: the host deliberately sends neither,
 * and picking one here would be inventing a fact it declined to state.
 */
export function matchDetail(hit: CustomerSearchHit): string {
  if (hit.matchedOn === 'Name') {
    return hit.serviceAddress ?? (hit.serviceAccountCount > 1 ? `${hit.serviceAccountCount} service accounts` : '—');
  }

  return hit.matchedValue;
}
