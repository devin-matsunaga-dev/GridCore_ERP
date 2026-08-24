import { Info } from 'lucide-react';
import type * as React from 'react';
import { Card, CardContent, CardFooter } from '@/components/ui/card';
import { Pagination } from '@/components/ui/pagination';
import { isWindowFull, registryWindow } from '@/api/registry';
import { formatCount } from '@/lib/format';
import { DataTable, type Column } from './data-table';
import { ErrorState } from './error-state';
import type { TableState } from './table-state';

/**
 * The card every registry table sits in: the filter row, the table, and the pagination footer —
 * plus the one notice that keeps the page honest about what it is showing.
 */

export type RegistryTableCardProps<TRow> = {
  /** The filter row. Rendered above the table, inside the card. */
  filters?: React.ReactNode;
  columns: readonly Column<TRow>[];
  table: TableState<TRow>;
  rowKey: (row: TRow) => string;
  label: string;
  isLoading?: boolean;
  /** A failed query. Replaces the table — a permission refusal is not an empty registry. */
  error?: unknown;
  onRetry?: () => void;
  empty?: React.ReactNode;
  onRowActivate?: (row: TRow) => void;
  isRowActive?: (row: TRow) => boolean;
  /** How many rows the server actually returned, before sorting and paging. */
  returnedRows?: number;
};

export function RegistryTableCard<TRow>({
  filters,
  columns,
  table,
  rowKey,
  label,
  isLoading = false,
  error,
  onRetry,
  empty,
  onRowActivate,
  isRowActive,
  returnedRows,
}: RegistryTableCardProps<TRow>) {
  const windowFull = !error && isWindowFull(returnedRows);
  const hasRows = !error && table.totalRows > 0;

  return (
    <Card>
      {filters && <div className="border-border border-b px-6 py-4">{filters}</div>}

      <CardContent className={filters ? 'pt-5' : 'pt-6'}>
        {windowFull && <WindowFullNotice />}

        {error ? (
          <ErrorState error={error} onRetry={onRetry} />
        ) : (
          <DataTable
            columns={columns}
            rows={table.pageRows}
            rowKey={rowKey}
            label={label}
            isLoading={isLoading}
            empty={empty}
            sort={table.sort}
            onSortChange={table.toggleSort}
            onRowActivate={onRowActivate}
            isRowActive={isRowActive}
          />
        )}
      </CardContent>

      {hasRows && !isLoading && (
        <CardFooter>
          <Pagination
            className="w-full"
            page={table.page}
            pageSize={table.pageSize}
            totalRows={table.totalRows}
            onPageChange={table.setPage}
            onPageSizeChange={table.setPageSize}
          />
        </CardFooter>
      )}
    </Card>
  );
}

/**
 * The list endpoints cap a page at `registryWindow` rows and report no total, so a full window may
 * have had rows cut off the end of it. Sorting and paging here would then be sorting and paging a
 * slice — which is fine, as long as the screen says so rather than passing it off as the registry.
 */
function WindowFullNotice() {
  return (
    <p className="bg-info-soft text-info mb-4 flex items-start gap-2 rounded-lg px-3 py-2 text-[13px]">
      <Info className="mt-px size-4 shrink-0" strokeWidth={1.75} aria-hidden="true" />
      <span>
        Showing the first {formatCount(registryWindow)} matches. Narrow the filters to be sure you are
        seeing everything.
      </span>
    </p>
  );
}
