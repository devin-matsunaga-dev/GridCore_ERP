import { describe, expect, it } from 'vitest';
import { compareValues, nextSort, sortRows, type SortValue } from './table-state';

type Row = { id: string; name: string; serial: string | null; onHand: number };

const rows: Row[] = [
  { id: '1', name: 'Recloser', serial: null, onHand: 3 },
  { id: '2', name: 'transformer', serial: 'TX-2', onHand: 10 },
  { id: '3', name: 'Pole', serial: 'TX-10', onHand: 3 },
];

const byName = (row: Row) => row.name;
const bySerial = (row: Row) => row.serial;
const byOnHand = (row: Row) => row.onHand;

describe('compareValues', () => {
  it('orders numbers numerically, not as text', () => {
    expect(compareValues(9, 10)).toBeLessThan(0);
  });

  it('orders registry numbers by their digits', () => {
    // C-000010 after C-000002: a plain string compare would put it before.
    expect(compareValues('C-000002', 'C-000010')).toBeLessThan(0);
  });

  it('ignores case, so a lower-cased name does not sort below every capital', () => {
    expect(compareValues('transformer', 'Vehicle')).toBeLessThan(0);
  });

  it('orders false before true', () => {
    expect(compareValues(false, true)).toBeLessThan(0);
  });
});

describe('sortRows', () => {
  it('sorts ascending and descending', () => {
    expect(sortRows(rows, byName, 'asc').map((row) => row.name)).toEqual([
      'Pole',
      'Recloser',
      'transformer',
    ]);
    expect(sortRows(rows, byName, 'desc').map((row) => row.name)).toEqual([
      'transformer',
      'Recloser',
      'Pole',
    ]);
  });

  it('sorts serial numbers by their digits', () => {
    expect(sortRows(rows, bySerial, 'asc').map((row) => row.serial)).toEqual(['TX-2', 'TX-10', null]);
  });

  /**
   * Failure path, and the reason the missing case is handled separately: reversing the sort must
   * not fill the top of the register with the poles and spans that carry no serial number.
   */
  it('keeps rows with no value at the bottom in both directions', () => {
    expect(sortRows(rows, bySerial, 'asc').at(-1)?.serial).toBeNull();
    expect(sortRows(rows, bySerial, 'desc').at(-1)?.serial).toBeNull();
  });

  /** Two rows that compare equal keep the server's order — newest first — rather than swapping. */
  it('is stable for equal values', () => {
    const equal = sortRows(rows, byOnHand, 'asc').filter((row) => row.onHand === 3);

    expect(equal.map((row) => row.id)).toEqual(['1', '3']);
  });

  it('does not mutate the array it was given', () => {
    const original = [...rows];
    sortRows(rows, byName, 'desc');

    expect(rows).toEqual(original);
  });

  it('handles an empty list', () => {
    expect(sortRows([] as Row[], byName, 'asc')).toEqual([]);
  });

  it('treats every value missing as no ordering at all', () => {
    const blanks: Row[] = [
      { id: 'a', name: 'a', serial: null, onHand: 0 },
      { id: 'b', name: 'b', serial: null, onHand: 0 },
    ];

    expect(sortRows(blanks, bySerial, 'desc').map((row) => row.id)).toEqual(['a', 'b']);
  });
});

describe('nextSort', () => {
  it('starts a new column ascending', () => {
    expect(nextSort(null, 'name')).toEqual({ key: 'name', direction: 'asc' });
    expect(nextSort({ key: 'status', direction: 'desc' }, 'name')).toEqual({
      key: 'name',
      direction: 'asc',
    });
  });

  it('reverses the column already sorted', () => {
    expect(nextSort({ key: 'name', direction: 'asc' }, 'name')).toEqual({
      key: 'name',
      direction: 'desc',
    });
    expect(nextSort({ key: 'name', direction: 'desc' }, 'name')).toEqual({
      key: 'name',
      direction: 'asc',
    });
  });
});

describe('compareValues with mixed shapes', () => {
  /** A column returning a number for one row and a string for another still has to order. */
  it('falls back to text when the types differ', () => {
    const mixed: SortValue[] = [10, '9'];

    expect(compareValues(mixed[0], mixed[1])).toBeGreaterThan(0);
  });
});
