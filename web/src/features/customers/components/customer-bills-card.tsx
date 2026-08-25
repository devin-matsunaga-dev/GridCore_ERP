import { ReceiptText } from 'lucide-react';
import type { Bill } from '@/api/billing';
import type { Column } from '@/components/registry/data-table';
import { EmptyState } from '@/components/registry/empty-state';
import { RegistryTableCard } from '@/components/registry/registry-table-card';
import { useTableState } from '@/components/registry/table-state';
import { StatusPill } from '@/components/ui/status';
import { formatDate, formatLabel, formatMoney, formatQuantity } from '@/lib/format';

/**
 * Every bill raised against this customer, as a table — sortable by column and paged in the
 * browser, over the window the host returned. The same shape every GridCore registry has, because
 * it is the same kind of thing: like-shaped rows a rep scans down.
 *
 * Read-only. The bill actions this table would otherwise offer — adjust, cancel, take a payment
 * against it — are Billing's own screens, and hanging them off the 360° page would put a second
 * copy of WP-2.4's permission-gated adjustment behind a tab.
 *
 * DRAFTS APPEAR AND SHOULD. A draft is owed by nobody, so it counts toward no balance on this page,
 * but every walk of the revenue-cycle demonstration leaves drafts behind for the other premises it
 * read, and a rep looking at a customer whose bill has not been issued yet is entitled to see that
 * it exists rather than to be told there are no bills.
 */
export function CustomerBillsCard({
  bills,
  isLoading,
  error,
  onRetry,
}: {
  bills: readonly Bill[];
  isLoading: boolean;
  error: unknown;
  onRetry: () => void;
}) {
  // Newest first before a column is chosen: the host orders by key, and a bill's id is a Guid v7.
  const table = useTableState({ rows: bills, columns: billColumns });

  return (
    <RegistryTableCard
      columns={billColumns}
      table={table}
      rowKey={(bill) => bill.id}
      label="Bills"
      isLoading={isLoading}
      error={error}
      onRetry={onRetry}
      returnedRows={bills.length}
      empty={
        <EmptyState
          icon={ReceiptText}
          title="No bills yet"
          message="A bill is raised when a billing run prices a meter reading for one of this customer's accounts."
        />
      }
    />
  );
}

/**
 * The identifier columns get their own space and refuse to wrap.
 *
 * A registry number is one token — "BIL-000001" broken across two lines reads as two numbers, and
 * stacking the bill over the account in one narrow column read as the same thing. Each is a column
 * of its own now; the table scrolls sideways inside its own container long before either wraps.
 */
const idColumn = 'whitespace-nowrap';

const billColumns: Column<Bill>[] = [
  {
    key: 'billNumber',
    header: 'Bill',
    sortValue: (bill) => bill.billNumber,
    headerClassName: idColumn,
    cellClassName: idColumn,
    cell: (bill) => <span className="text-muted tabular text-xs font-medium">{bill.billNumber}</span>,
  },
  {
    key: 'accountNumber',
    header: 'Account',
    sortValue: (bill) => bill.accountNumber,
    headerClassName: idColumn,
    cellClassName: idColumn,
    cell: (bill) => <span className="text-muted tabular text-xs">{bill.accountNumber}</span>,
  },
  {
    key: 'period',
    header: 'Period',
    wide: true,
    // Sorted on the end of the period, which is the date a reader means by "which bill is later".
    sortValue: (bill) => bill.periodEnd,
    cell: (bill) => (
      <span className="block min-w-0">
        <span className="text-heading block truncate font-medium">
          {formatDate(bill.periodStart)} – {formatDate(bill.periodEnd)}
        </span>
        <span className="text-muted block truncate text-xs">
          {formatQuantity(bill.consumption)} {bill.unitOfMeasure} · {bill.ratePlanCode}
        </span>
      </span>
    ),
  },
  {
    key: 'issuedOn',
    header: 'Issued',
    sortValue: (bill) => bill.issuedOn,
    headerClassName: idColumn,
    cellClassName: idColumn,
    cell: (bill) => (
      <span className="block">
        <span className="text-muted block">{bill.issuedOn ? formatDate(bill.issuedOn) : '—'}</span>
        {bill.dueDate && (
          <span className="text-muted block text-xs">due {formatDate(bill.dueDate)}</span>
        )}
      </span>
    ),
  },
  {
    key: 'amountDue',
    header: 'Amount due',
    align: 'right',
    sortValue: (bill) => bill.amountDue,
    headerClassName: idColumn,
    cellClassName: idColumn,
    cell: (bill) => (
      <span className="block">
        <span className="tabular block">{formatMoney(bill.amountDue)}</span>
        {bill.adjustmentTotal !== 0 && (
          <span className="text-muted tabular block text-xs">
            {formatMoney(bill.adjustmentTotal)} adj.
          </span>
        )}
      </span>
    ),
  },
  {
    key: 'balance',
    header: 'Outstanding',
    align: 'right',
    // The bill's own `balance`, never `totalAmount - amountPaid`: the printed total does not move
    // when a bill is corrected, and subtracting from it would quietly ignore every adjustment.
    sortValue: (bill) => bill.balance,
    headerClassName: idColumn,
    cellClassName: idColumn,
    cell: (bill) => (
      <span className={bill.isOutstanding ? 'text-heading tabular font-medium' : 'text-muted tabular'}>
        {formatMoney(bill.balance)}
      </span>
    ),
  },
  {
    key: 'status',
    header: 'Status',
    sortValue: (bill) => bill.status,
    headerClassName: idColumn,
    cellClassName: idColumn,
    cell: (bill) => <StatusPill status={formatLabel(bill.status)} />,
  },
];
