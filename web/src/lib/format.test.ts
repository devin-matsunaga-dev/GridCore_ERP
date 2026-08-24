import { describe, expect, it } from 'vitest';
import {
  formatCount,
  formatMoney,
  formatMoneyCompact,
  formatPercent,
  formatRelativeTime,
  niceTicks,
} from './format';

describe('formatting', () => {
  it('renders money with a currency prefix and thousands separators', () => {
    expect(formatMoney(1234567.5)).toBe('$1,234,567.50');
  });

  it('rounds money to two places rather than dropping cents', () => {
    expect(formatMoney(0.005)).toBe('$0.01');
  });

  it('renders the compact KPI form', () => {
    expect(formatMoneyCompact(24_800_000)).toBe('$24.8M');
  });

  it('drops a trailing zero by default, so an axis reads $0 and $8M', () => {
    expect(formatMoneyCompact(0)).toBe('$0');
    expect(formatMoneyCompact(8_000_000)).toBe('$8M');
  });

  /** A row of figures has to line up: $21M beside $18.6M breaks the column. */
  it('forces decimals when asked', () => {
    expect(formatMoneyCompact(21_000_000, 1)).toBe('$21.0M');
    expect(formatMoneyCompact(18_600_000, 1)).toBe('$18.6M');
  });

  it('renders counts with separators', () => {
    expect(formatCount(12842)).toBe('12,842');
  });

  it('renders a fraction as a one-decimal percentage', () => {
    expect(formatPercent(0.064)).toBe('6.4%');
  });

  it('rounds to a whole percentage when asked', () => {
    expect(formatPercent(645 / 1286, 0)).toBe('50%');
  });

  describe('niceTicks', () => {
    it('rounds a chart axis up to readable steps', () => {
      expect(niceTicks(2_950_000)).toEqual([0, 1_000_000, 2_000_000, 3_000_000]);
      expect(niceTicks(21_000_000)).toEqual([0, 8_000_000, 16_000_000, 24_000_000]);
    });

    it('always covers the maximum value', () => {
      for (const max of [7, 42, 137, 1_499, 8_800_000]) {
        expect(niceTicks(max).at(-1)!).toBeGreaterThanOrEqual(max);
      }
    });

    /** Failure path: an empty series must not produce NaN ticks. */
    it('degenerates safely for a zero or missing maximum', () => {
      expect(niceTicks(0)).toEqual([0]);
      expect(niceTicks(Number.NaN)).toEqual([0]);
    });
  });

  describe('relative time', () => {
    const now = new Date('2026-08-24T12:00:00Z');

    it.each([
      [new Date('2026-08-24T11:50:00Z'), '10m ago'],
      [new Date('2026-08-24T11:00:00Z'), '1h ago'],
      [new Date('2026-08-24T09:00:00Z'), '3h ago'],
      [new Date('2026-08-22T12:00:00Z'), '2d ago'],
    ])('renders %s as %s', (occurredAt, expected) => {
      expect(formatRelativeTime(occurredAt, now)).toBe(expected);
    });

    it('reads a future timestamp forwards rather than as a negative age', () => {
      expect(formatRelativeTime(new Date('2026-08-24T14:00:00Z'), now)).toBe('in 2h');
    });
  });
});
