import { describe, expect, it } from 'vitest';
import { centsEqual, fromCents, isWholeCents, toCents } from './money';

/**
 * The arithmetic three features now share. `registration.ts` and `revenue-cycle.ts` re-export from
 * here, so their own tests still cover these in the context that motivated them; this pins the
 * promoted module directly.
 */

describe('money', () => {
  it('rounds to whole cents in both directions', () => {
    expect(toCents(63.62)).toBe(6362);
    expect(fromCents(6362)).toBe(63.62);
    expect(toCents(0.005)).toBe(1);
    expect(toCents(-20)).toBe(-2000);
  });

  it('compares to the cent rather than by value', () => {
    // The comparison the whole module exists for: these are not `===`, and a screen must not care.
    expect(0.1 + 0.2 === 0.3).toBe(false);
    expect(centsEqual(0.1 + 0.2, 0.3)).toBe(true);

    expect(centsEqual(63.62, 63.63)).toBe(false);
  });

  it('knows an amount that is finer than a cent', () => {
    expect(isWholeCents(75)).toBe(true);
    expect(isWholeCents(75.25)).toBe(true);
    expect(isWholeCents(75.255)).toBe(false);
  });
});
