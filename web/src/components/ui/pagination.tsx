import { ChevronLeft, ChevronRight } from 'lucide-react';
import { cn } from '@/lib/utils';
import { formatCount } from '@/lib/format';
import { Select } from './select';

export const rowsPerPageOptions = [5, 10, 25] as const;

export type PaginationProps = {
  page: number;
  pageSize: number;
  totalRows: number;
  onPageChange: (page: number) => void;
  onPageSizeChange?: (pageSize: number) => void;
  className?: string;
};

/** Total pages for a row count, never below 1 so an empty table still renders page 1 of 1. */
export function pageCount(totalRows: number, pageSize: number): number {
  return Math.max(1, Math.ceil(totalRows / pageSize));
}

/** The `1–5 of 20` label; the range is clamped so the last page never overstates the count. */
export function rangeLabel(page: number, pageSize: number, totalRows: number): string {
  if (totalRows === 0) return '0 of 0';

  const first = (page - 1) * pageSize + 1;
  const last = Math.min(page * pageSize, totalRows);

  return `${formatCount(first)}–${formatCount(last)} of ${formatCount(totalRows)}`;
}

export function Pagination({
  page,
  pageSize,
  totalRows,
  onPageChange,
  onPageSizeChange,
  className,
}: PaginationProps) {
  const pages = pageCount(totalRows, pageSize);

  return (
    <div className={cn('flex flex-wrap items-center justify-between gap-x-3 gap-y-2', className)}>
      <p className="text-muted tabular text-xs">{rangeLabel(page, pageSize, totalRows)}</p>

      <div className="flex items-center gap-0.5">
        <PageButton label="Previous page" disabled={page <= 1} onClick={() => onPageChange(page - 1)}>
          <ChevronLeft className="size-4" strokeWidth={1.75} aria-hidden="true" />
        </PageButton>

        {Array.from({ length: pages }, (_, index) => index + 1).map((number) => (
          <button
            key={number}
            type="button"
            aria-label={`Page ${number}`}
            aria-current={number === page ? 'page' : undefined}
            onClick={() => onPageChange(number)}
            className={cn(
              'rounded-field tabular size-7 text-xs font-medium transition-colors',
              number === page
                ? 'border-primary text-primary border'
                : 'text-body hover:bg-canvas border border-transparent',
            )}
          >
            {number}
          </button>
        ))}

        <PageButton label="Next page" disabled={page >= pages} onClick={() => onPageChange(page + 1)}>
          <ChevronRight className="size-4" strokeWidth={1.75} aria-hidden="true" />
        </PageButton>
      </div>

      {onPageSizeChange && (
        <label className="text-muted flex items-center gap-1.5 text-xs whitespace-nowrap">
          Rows per page:
          <Select
            className="h-7 py-0.5 pr-6 pl-2 text-xs"
            value={pageSize}
            onChange={(event) => onPageSizeChange(Number(event.target.value))}
            aria-label="Rows per page"
          >
            {rowsPerPageOptions.map((size) => (
              <option key={size} value={size}>
                {size}
              </option>
            ))}
          </Select>
        </label>
      )}
    </div>
  );
}

function PageButton({
  label,
  disabled,
  onClick,
  children,
}: {
  label: string;
  disabled: boolean;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      aria-label={label}
      disabled={disabled}
      onClick={onClick}
      className="text-body hover:bg-canvas rounded-field flex size-7 items-center justify-center transition-colors disabled:pointer-events-none disabled:opacity-40"
    >
      {children}
    </button>
  );
}
