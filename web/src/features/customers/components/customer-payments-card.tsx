import { Wallet } from 'lucide-react';
import { paymentMethodLabels, type Payment, type PaymentMethod } from '@/api/payments';
import type { Column } from '@/components/registry/data-table';
import { EmptyState } from '@/components/registry/empty-state';
import { RegistryTableCard } from '@/components/registry/registry-table-card';
import { useTableState } from '@/components/registry/table-state';
import { StatusPill } from '@/components/ui/status';
import { formatDateTime, formatLabel, formatMoney } from '@/lib/format';

/**
 * Every payment attempt against this customer's bills, as a table — sortable and paged in the
 * browser, the same shape the bills table and every registry has.
 *
 * A DECLINE IS A ROW HERE. Payments records the attempt whether or not the provider approved it
 * (which is why a refusal is a 200 with `approved: false` rather than a 4xx), and a refusal from
 * this morning is the answer to "I paid that, why am I being chased". Filtering the table down to
 * settled money would delete the half of the register a rep needs most — so the amount is muted
 * rather than absent on an attempt that did not settle, and the provider's own message is on the
 * row, because "The card was declined" is something a rep can read back down the phone.
 */
export function CustomerPaymentsCard({
  payments,
  isLoading,
  error,
  onRetry,
}: {
  payments: readonly Payment[];
  isLoading: boolean;
  error: unknown;
  onRetry: () => void;
}) {
  const table = useTableState({ rows: payments, columns: paymentColumns });

  return (
    <RegistryTableCard
      columns={paymentColumns}
      table={table}
      rowKey={(payment) => payment.id}
      label="Payments"
      isLoading={isLoading}
      error={error}
      onRetry={onRetry}
      returnedRows={payments.length}
      empty={
        <EmptyState
          icon={Wallet}
          title="No payments yet"
          message="Attempts appear here whether or not the provider approved them — a refusal is a record, not a gap."
        />
      }
    />
  );
}

/** When the attempt happened: settled if it did, requested otherwise. A decline never settles. */
function takenAt(payment: Payment): string {
  return payment.settledAt ?? payment.requestedAt;
}

/**
 * The identifier columns get their own space and refuse to wrap — same call the bills table makes.
 * A registry number is one token, and "PAY-000001" broken across two lines reads as two numbers.
 */
const idColumn = 'whitespace-nowrap';

const paymentColumns: Column<Payment>[] = [
  {
    key: 'paymentNumber',
    header: 'Payment',
    sortValue: (payment) => payment.paymentNumber,
    headerClassName: idColumn,
    cellClassName: idColumn,
    cell: (payment) => (
      <span className="text-muted tabular text-xs font-medium">{payment.paymentNumber}</span>
    ),
  },
  {
    key: 'billNumber',
    header: 'Bill',
    sortValue: (payment) => payment.billNumber,
    headerClassName: idColumn,
    cellClassName: idColumn,
    cell: (payment) => <span className="text-muted tabular text-xs">{payment.billNumber}</span>,
  },
  {
    key: 'takenAt',
    header: 'Taken',
    wide: true,
    sortValue: takenAt,
    cell: (payment) => (
      <span className="block min-w-0">
        <span className="text-heading block truncate font-medium">{formatDateTime(takenAt(payment))}</span>
        {payment.providerMessage && (
          <span className="text-muted block truncate text-xs">{payment.providerMessage}</span>
        )}
      </span>
    ),
  },
  {
    key: 'method',
    header: 'Method',
    sortValue: (payment) => payment.method,
    headerClassName: idColumn,
    cellClassName: idColumn,
    cell: (payment) => (
      <span className="block">
        <span className="block">{methodLabel(payment.method)}</span>
        {payment.instrument && (
          <span className="text-muted tabular block text-xs">{payment.instrument}</span>
        )}
      </span>
    ),
  },
  {
    key: 'amount',
    header: 'Amount',
    align: 'right',
    sortValue: (payment) => payment.amount,
    headerClassName: idColumn,
    cellClassName: idColumn,
    cell: (payment) => (
      <span className={payment.isSettled ? 'text-heading tabular font-medium' : 'text-muted tabular'}>
        {formatMoney(payment.amount)}
      </span>
    ),
  },
  {
    key: 'status',
    header: 'Status',
    sortValue: (payment) => payment.status,
    headerClassName: idColumn,
    cellClassName: idColumn,
    cell: (payment) => <StatusPill status={formatLabel(payment.status)} />,
  },
];

/**
 * The method as a label, falling back to the wire value.
 *
 * `Payment.method` is a plain string on the wire rather than the `PaymentMethod` union — the host
 * sends what was recorded, and a method added there before it is added here should read as itself
 * rather than as an empty cell.
 */
function methodLabel(method: string): string {
  return paymentMethodLabels[method as PaymentMethod] ?? formatLabel(method);
}
