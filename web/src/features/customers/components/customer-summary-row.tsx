import { AlarmClock, PiggyBank, ReceiptText, Scale, Wallet } from 'lucide-react';
import type { Customer } from '@/api/customers';
import type { Payment } from '@/api/payments';
import { formatCount, formatDate, formatMoney } from '@/lib/format';
import { toCents } from '@/lib/money';
import { lastSettledPayment, type CustomerBalance } from '../customer-360';
import { SummaryTile } from './summary-tile';

/**
 * DESIGN.md's KPI row, answering the five questions a rep is asked before they are asked anything
 * else: what is owed, what is late, what has been billed, what deposit is held, and when money last
 * arrived.
 *
 * Every figure but the deposit comes from Billing's own bills, so the row is empty-but-valid for a
 * prospect nobody has billed yet — five zeros, which is the true answer rather than a blank strip.
 * The deposit is the customer record's, because that is where WP-2.8 recorded it; WP-2.12 turns it
 * into a tracked balance and this tile is what it will report against.
 */
export function CustomerSummaryRow({
  customer,
  balance,
  payments,
  isPending,
}: {
  customer: Customer;
  balance: CustomerBalance;
  payments: readonly Payment[];
  /** The bills and payments behind the figures are still arriving. */
  isPending: boolean;
}) {
  const owed = toCents(balance.outstanding);
  const overdue = toCents(balance.overdue);
  const latest = lastSettledPayment(payments);

  return (
    <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-5">
      <SummaryTile
        label="Current balance"
        value={formatMoney(balance.outstanding)}
        icon={Scale}
        tone={owed > 0 ? 'danger' : owed < 0 ? 'success' : undefined}
        detail={
          owed === 0
            ? 'Nothing owed'
            : owed < 0
              ? 'In credit'
              : `${formatCount(balance.outstandingBills)} ${balance.outstandingBills === 1 ? 'bill' : 'bills'} open`
        }
        isPending={isPending}
      />

      <SummaryTile
        label="Overdue"
        value={formatMoney(balance.overdue)}
        icon={AlarmClock}
        tone={overdue > 0 ? 'danger' : undefined}
        detail={overdue > 0 ? 'Past the due date' : 'Nothing past due'}
        isPending={isPending}
      />

      <SummaryTile
        label="Billed to date"
        value={formatMoney(balance.netBilled)}
        icon={ReceiptText}
        detail={
          toCents(balance.adjustments) === 0
            ? 'Issued bills, as printed'
            : `Includes ${formatMoney(balance.adjustments)} in corrections`
        }
        isPending={isPending}
      />

      <SummaryTile
        label="Deposit held"
        value={formatMoney(customer.depositHeld)}
        icon={PiggyBank}
        detail={toCents(customer.depositHeld) === 0 ? 'None collected' : 'On account'}
      />

      <SummaryTile
        label="Last payment"
        value={latest ? formatMoney(latest.amount) : formatMoney(0)}
        icon={Wallet}
        detail={latest?.settledAt ? formatDate(latest.settledAt) : 'None settled yet'}
        isPending={isPending}
      />
    </div>
  );
}
