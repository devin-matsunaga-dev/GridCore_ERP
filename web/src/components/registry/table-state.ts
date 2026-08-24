import { useState } from 'react';
import { pageCount } from '@/components/ui/pagination';

/**
 * Sorting and paging for the registry tables.
 *
 * Both are client-side over the window the server returned, deliberately: the list endpoints take
 * their filters as query parameters but offer no sort and no offset (see `api/registry.ts`), so
 * the search that decides *which* rows exist happens on the server and the ordering of the ones
 * that came back happens here. Nothing in this file touches the network.
 */

export type SortDirection = 'asc' | 'desc';

export type SortState = { key: string; direction: SortDirection };

/** What a column sorts on. `null`/`undefined` is "not recorded", which is not a value. */
export type SortValue = string | number | boolean | null | undefined;

function isMissing(value: SortValue): value is null | undefined {
  return value === null || value === undefined;
}

/**
 * Orders two cell values ascending. Missing values are **not** ordered — the caller keeps them at
 * the bottom in both directions, because a serial number nobody recorded is not "the smallest
 * serial number", and reversing the sort should not fill the top of the table with blanks.
 */
export function compareValues(a: SortValue, b: SortValue): number {
  if (typeof a === 'number' && typeof b === 'number') return a - b;
  if (typeof a === 'boolean' && typeof b === 'boolean') return Number(a) - Number(b);

  // `numeric` so C-000002 sorts before C-000010, and the registry numbers read in issue order.
  return String(a).localeCompare(String(b), undefined, { numeric: true, sensitivity: 'base' });
}

/**
 * A stable sort: rows comparing equal keep the server's order, which is newest-first. Without the
 * index tiebreak two customers registered in the same second would swap places between renders.
 */
export function sortRows<TRow>(
  rows: readonly TRow[],
  sortValue: (row: TRow) => SortValue,
  direction: SortDirection,
): TRow[] {
  const sign = direction === 'asc' ? 1 : -1;

  return rows
    .map((row, index) => ({ row, index, value: sortValue(row) }))
    .toSorted((left, right) => {
      if (isMissing(left.value) || isMissing(right.value)) {
        // Missing sinks in both directions; two missing values fall through to the tiebreak.
        if (!isMissing(right.value)) return 1;
        if (!isMissing(left.value)) return -1;
        return left.index - right.index;
      }

      return compareValues(left.value, right.value) * sign || left.index - right.index;
    })
    .map((entry) => entry.row);
}

/** Clicking a column header: a new column starts ascending, the current one reverses. */
export function nextSort(current: SortState | null, key: string): SortState {
  return current?.key === key
    ? { key, direction: current.direction === 'asc' ? 'desc' : 'asc' }
    : { key, direction: 'asc' };
}

/**
 * The part of a column this file needs. `Column<TRow>` from `data-table.tsx` satisfies it
 * structurally, so a page declares each column's ordering once, beside the cell it orders — there
 * is no second table of accessors to keep in step with the first.
 */
export type SortableColumn<TRow> = {
  key: string;
  sortValue?: (row: TRow) => SortValue;
};

export type TableStateOptions<TRow> = {
  rows: readonly TRow[] | undefined;
  columns: readonly SortableColumn<TRow>[];
  initialSort?: SortState;
  initialPageSize?: number;
};

export type TableState<TRow> = {
  sort: SortState | null;
  toggleSort: (key: string) => void;
  page: number;
  setPage: (page: number) => void;
  pageSize: number;
  setPageSize: (size: number) => void;
  /** The rows on the current page, sorted. */
  pageRows: TRow[];
  /** Every row the server returned that survived the filters — what the count in the header says. */
  totalRows: number;
};

export function useTableState<TRow>({
  rows,
  columns,
  initialSort,
  initialPageSize = 10,
}: TableStateOptions<TRow>): TableState<TRow> {
  const [sort, setSort] = useState<SortState | null>(initialSort ?? null);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(initialPageSize);

  // Sorted on every render rather than memoised: the column list is rebuilt by the caller each
  // time, so a dependency array over it would either lie or never hit. The window is at most
  // `registryWindow` rows, which is nothing to sort.
  const source = rows ?? [];
  const sortValue = sort ? columns.find((column) => column.key === sort.key)?.sortValue : undefined;
  const sorted = sort && sortValue ? sortRows(source, sortValue, sort.direction) : [...source];

  // Clamped during render rather than in an effect: a filter that shrinks the list must not paint
  // one frame of an empty page 4 before an effect pulls it back.
  const currentPage = Math.min(page, pageCount(sorted.length, pageSize));

  return {
    sort,
    toggleSort: (key) => {
      setSort((current) => nextSort(current, key));
      setPage(1);
    },
    page: currentPage,
    setPage,
    pageSize,
    setPageSize: (size) => {
      setPageSize(size);
      setPage(1);
    },
    pageRows: sorted.slice((currentPage - 1) * pageSize, currentPage * pageSize),
    totalRows: sorted.length,
  };
}
