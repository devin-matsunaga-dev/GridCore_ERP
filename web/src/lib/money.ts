/**
 * Money arithmetic in whole cents.
 *
 * These amounts are `decimal` on the wire and `number` the moment `JSON.parse` has had them, so
 * every comparison and every running total goes through cents rather than through `===` and `+` on
 * the parsed value. 0.1 + 0.2 is a poor reason to tell a clerk their deposit is wrong, or a rep
 * that a customer owes a third of a cent.
 *
 * The third caller is what promoted this out of the two features that each had a copy — the rule
 * `IntakeField` is waiting on, applied. `registration.ts` and `revenue-cycle.ts` re-export their
 * own names from here, so nothing that imported them had to change.
 */

/** An amount as a whole number of cents. */
export function toCents(amount: number): number {
  return Math.round(amount * 100);
}

/** Cents back to the amount they stand for. */
export function fromCents(cents: number): number {
  return cents / 100;
}

/** Money equality to the cent — the smallest unit anything in GridCore is stored to. */
export function centsEqual(left: number, right: number): boolean {
  return toCents(left) === toCents(right);
}

/** Whether an amount somebody typed is exact to the cent — refused rather than rounded, as the host does. */
export function isWholeCents(amount: number): boolean {
  return Math.abs(amount * 100 - toCents(amount)) < 1e-6;
}
