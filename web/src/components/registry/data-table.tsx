import { ArrowDown, ArrowUp, ChevronsUpDown, type LucideIcon } from 'lucide-react';
import type * as React from 'react';
import { Skeleton } from '@/components/ui/skeleton';
import { cn } from '@/lib/utils';
import type { SortState, SortValue } from './table-state';

/**
 * The registry table. DESIGN.md density: compact rows, muted uppercase headers, no zebra, a canvas
 * tint on hover, status as pills inline, numerics right-aligned and the ID column muted.
 *
 * Presentational on purpose — sorting and paging live in `useTableState`, so both are testable
 * without a DOM and a page can put the pagination wherever its card wants it.
 */

export type Column<TRow> = {
  /** Stable key: the sort state and the activation target are named by it. */
  key: string;
  header: string;
  cell: (row: TRow) => React.ReactNode;
  /** Omit to make the column unsortable — a header with no ordering worth offering. */
  sortValue?: (row: TRow) => SortValue;
  align?: 'left' | 'right';
  /** The column that absorbs the slack, so it truncates last rather than first. */
  wide?: boolean;
  /**
   * The cell that becomes the control opening the row's detail. Falls back to the `wide` column,
   * then the first — a row is never activatable by mouse only.
   */
  primary?: boolean;
  headerClassName?: string;
  cellClassName?: string;
};

export type DataTableProps<TRow> = {
  columns: readonly Column<TRow>[];
  rows: readonly TRow[];
  rowKey: (row: TRow) => string;
  /** What the table is, for screen readers — "Customers", "Assets". */
  label: string;
  isLoading?: boolean;
  /** Rendered instead of rows when there are none and nothing is loading. */
  empty?: React.ReactNode;
  sort?: SortState | null;
  onSortChange?: (key: string) => void;
  /** Makes each row open a detail: a button in the primary cell, plus a click on the row. */
  onRowActivate?: (row: TRow) => void;
  /** Marks the row whose detail is open, so the table shows what the drawer is showing. */
  isRowActive?: (row: TRow) => boolean;
  className?: string;
};

const skeletonRowCount = 5;

/**
 * Moves the keyboard between the row buttons of a table body.
 *
 * Down and Up walk the rows a rep can activate; Home and End jump to the ends. Clamped rather than
 * wrapped: a table is one page of a longer register, so wrapping from the last row to the first
 * would leave both arrows going somewhere unexpected.
 *
 * The buttons keep their natural tab order — this is on top of Tab, not instead of it — so a screen
 * reader user crossing the table cell by cell is unaffected, and a rep who typed in the filter above
 * can reach the answer with two keys (WP-2.9's keyboard-first search).
 */
export function rowButtonToMove(
  buttons: readonly HTMLElement[],
  from: HTMLElement | null,
  key: string,
): HTMLElement | undefined {
  if (buttons.length === 0) return undefined;

  const current = from ? buttons.indexOf(from) : -1;

  switch (key) {
    case 'ArrowDown':
      return buttons[Math.min(current + 1, buttons.length - 1)];
    case 'ArrowUp':
      // From outside the table (the filter box above it), Up is not an entry point — only Down is.
      return current <= 0 ? undefined : buttons[current - 1];
    case 'Home':
      return current < 0 ? undefined : buttons[0];
    case 'End':
      return current < 0 ? undefined : buttons[buttons.length - 1];
    default:
      return undefined;
  }
}

export function DataTable<TRow>({
  columns,
  rows,
  rowKey,
  label,
  isLoading = false,
  empty,
  sort,
  onSortChange,
  onRowActivate,
  isRowActive,
  className,
}: DataTableProps<TRow>) {
  if (!isLoading && rows.length === 0 && empty) {
    return <>{empty}</>;
  }

  const activationKey = onRowActivate ? activationColumnKey(columns) : undefined;

  return (
    // The one scrollable axis a table is allowed: DESIGN.md's quality floor puts wide content in
    // its own overflow container so the page body never scrolls sideways.
    <div className={cn('scrollbar-subtle -mx-6 overflow-x-auto px-6', className)}>
      <table className="w-full min-w-[36rem] border-collapse text-sm">
        <caption className="sr-only">{label}</caption>
        <thead>
          <tr className="border-border border-b">
            {columns.map((column) => (
              <HeaderCell
                key={column.key}
                column={column}
                sort={sort ?? null}
                onSortChange={onSortChange}
              />
            ))}
          </tr>
        </thead>
        <tbody
          // Arrow keys walk the rows; Enter and Space activate, because the target is a real button.
          onKeyDown={
            onRowActivate
              ? (event) => {
                  const body = event.currentTarget;
                  const buttons = [...body.querySelectorAll<HTMLElement>('button[data-row-activate]')];
                  const target = rowButtonToMove(
                    buttons,
                    event.target instanceof HTMLElement ? event.target.closest('button[data-row-activate]') : null,
                    event.key,
                  );

                  if (target) {
                    event.preventDefault();
                    target.focus();
                  }
                }
              : undefined
          }
        >
          {isLoading
            ? Array.from({ length: skeletonRowCount }, (_, index) => (
                <tr key={index} className="border-border border-b last:border-0">
                  {columns.map((column) => (
                    <td key={column.key} className="px-2 py-3 first:pl-0 last:pr-0">
                      <Skeleton className="h-3.5 w-full" />
                    </td>
                  ))}
                </tr>
              ))
            : rows.map((row) => (
                <Row
                  key={rowKey(row)}
                  row={row}
                  columns={columns}
                  onRowActivate={onRowActivate}
                  activationKey={activationKey}
                  isActive={isRowActive?.(row) ?? false}
                />
              ))}
        </tbody>
      </table>
    </div>
  );
}

function HeaderCell<TRow>({
  column,
  sort,
  onSortChange,
}: {
  column: Column<TRow>;
  sort: SortState | null;
  onSortChange?: (key: string) => void;
}) {
  const sortable = Boolean(column.sortValue && onSortChange);
  const active = sort?.key === column.key;
  const Icon: LucideIcon = active ? (sort.direction === 'asc' ? ArrowUp : ArrowDown) : ChevronsUpDown;

  const className = cn(
    'text-muted px-2 pb-2.5 text-[11px] font-medium tracking-[0.06em] whitespace-nowrap uppercase first:pl-0 last:pr-0',
    column.align === 'right' ? 'text-right' : 'text-left',
    column.wide && 'w-full',
    column.headerClassName,
  );

  if (!sortable) {
    return (
      <th scope="col" className={className}>
        {column.header}
      </th>
    );
  }

  return (
    <th
      scope="col"
      className={className}
      // The header carries the direction so a screen reader announces the ordering, not just the name.
      aria-sort={active ? (sort.direction === 'asc' ? 'ascending' : 'descending') : 'none'}
    >
      <button
        type="button"
        onClick={() => onSortChange?.(column.key)}
        className={cn(
          'rounded-field group -mx-1 inline-flex items-center gap-1 px-1 py-0.5 transition-colors',
          'hover:text-heading',
          column.align === 'right' && 'flex-row-reverse',
          active && 'text-heading',
        )}
      >
        {column.header}
        <Icon
          className={cn('size-3 shrink-0 transition-opacity', active ? 'opacity-100' : 'opacity-0 group-hover:opacity-60')}
          strokeWidth={2}
          aria-hidden="true"
        />
      </button>
    </th>
  );
}

/**
 * Which cell carries the control that opens the detail. One real button inside the row, rather than
 * `role="button"` on the `<tr>` itself: overriding the row's role takes it out of the table for a
 * screen reader, so the register stops being navigable as a table at all.
 */
function activationColumnKey<TRow>(columns: readonly Column<TRow>[]): string | undefined {
  const target = columns.find((column) => column.primary) ?? columns.find((column) => column.wide) ?? columns[0];

  return target?.key;
}

function Row<TRow>({
  row,
  columns,
  onRowActivate,
  activationKey,
  isActive,
}: {
  row: TRow;
  columns: readonly Column<TRow>[];
  onRowActivate?: (row: TRow) => void;
  activationKey: string | undefined;
  isActive: boolean;
}) {
  const activatable = Boolean(onRowActivate);

  return (
    <tr
      // The click is a mouse convenience on top of the button below; the row keeps its row role.
      onClick={activatable ? () => onRowActivate?.(row) : undefined}
      aria-current={isActive ? 'true' : undefined}
      className={cn(
        'border-border border-b transition-colors last:border-0',
        activatable && 'hover:bg-canvas has-[:focus-visible]:bg-canvas cursor-pointer',
        isActive && 'bg-primary-soft/60',
      )}
    >
      {columns.map((column) => (
        <td
          key={column.key}
          className={cn(
            'text-body px-2 py-3 text-[13px] first:pl-0 last:pr-0',
            column.align === 'right' ? 'tabular text-right' : 'text-left',
            column.wide && 'max-w-0 truncate',
            column.cellClassName,
          )}
        >
          {activatable && column.key === activationKey ? (
            <button
              type="button"
              data-row-activate=""
              // Stopped so the row's own click does not fire the same activation twice.
              onClick={(event) => {
                event.stopPropagation();
                onRowActivate?.(row);
              }}
              className="rounded-field block w-full min-w-0 text-left"
            >
              {column.cell(row)}
            </button>
          ) : (
            column.cell(row)
          )}
        </td>
      ))}
    </tr>
  );
}
