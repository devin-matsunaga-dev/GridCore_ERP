import { zodResolver } from '@hookform/resolvers/zod';
import { useMutation } from '@tanstack/react-query';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { billingApi, type Bill } from '@/api/billing';
import {
  paymentMethodLabels,
  paymentMethods,
  paymentsApi,
  type PaymentMethod,
  type TakePaymentResult,
} from '@/api/payments';
import { toast } from '@/components/feedback/toast';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { StatusPill, toneFor } from '@/components/ui/status';
import { formatDateTime, formatLabel, formatMoney } from '@/lib/format';
import { centsEqual } from '../revenue-cycle';
import { StepFacts, StepField, StepFields } from './step-card';

/**
 * SPEC steps 7 and 8 — Run Simulated Payment, Update Balance.
 *
 * The provider answers through `IPaymentProvider`, and its answer is **not** a status: a decline
 * and insufficient funds both land on `Declined`, while a timeout lands on `Failed` because the
 * money may have moved and the answer been lost. Both are shown.
 *
 * Updating the balance is not a second button either, and could not be: Payments publishes
 * `PaymentApproved` and **Billing's** consumer reduces the balance in a schema Payments has never
 * heard of. So this step takes the payment and then watches for the bill to move, which is the
 * asynchronous cross-module effect the whole architecture is built around — and the wait is what
 * makes it visible rather than instant and unexplained.
 */

/** How long to watch for Billing's consumer before saying so. A ceiling, never a pause. */
const settlementTimeoutMs = 20_000;
const pollIntervalMs = 400;

const schema = z.object({
  method: z.enum(paymentMethods),
  instrument: z.string().trim().max(64).optional(),
  amount: z.coerce.number({ message: 'A payment is an amount.' }).positive('A payment is more than nothing.'),
});

type Values = z.input<typeof schema>;

export type PaymentStepResult = { payment: TakePaymentResult; settledBill: Bill };

export function PaymentStep({
  bill,
  result,
  onDone,
}: {
  bill: Bill;
  result?: PaymentStepResult;
  onDone: (result: PaymentStepResult) => void;
}) {
  const form = useForm({
    resolver: zodResolver(schema),
    defaultValues: { method: 'card' as PaymentMethod, instrument: '•••• 4242', amount: bill.balance } as Values,
  });

  const method = useWatch({ control: form.control, name: 'method' });

  const take = useMutation({
    mutationFn: async (values: z.output<typeof schema>) => {
      const payment = await paymentsApi.take({
        billId: bill.id,
        amount: values.amount,
        method: values.method,
        // Ignored for cash, and not sent for it: the money is already in the drawer.
        instrument: values.method === 'cash' ? null : values.instrument || null,
      });

      // Approved money moves the bill through the broker, so the bill is re-read until it does.
      // A refusal publishes nothing, so re-reading it once is the whole story.
      const settledBill = payment.approved
        ? await awaitSettlement(bill.id, bill.amountPaid)
        : await billingApi.get(bill.id);

      return { payment, settledBill };
    },
    onSuccess: (taken) => {
      if (taken.payment.approved) {
        toast.success(
          `Payment ${taken.payment.payment.paymentNumber} approved`,
          `${formatMoney(taken.payment.payment.amount)} · balance now ${formatMoney(taken.settledBill.balance)}`,
        );
      } else {
        // A refusal is an answer, not an error — it is recorded, it is audited, and it is why the
        // customer still owes money. Reported as a warning rather than thrown.
        toast.warning(
          `Payment ${taken.payment.payment.paymentNumber} ${taken.payment.payment.outcome?.toLowerCase() ?? 'refused'}`,
          taken.payment.payment.providerMessage ?? 'The bill is still owed.',
        );
      }

      onDone(taken);
    },
    onError: (error) => toast.apiError(error, 'The payment could not be taken.'),
  });

  if (result) {
    const { payment, settledBill } = result;
    const settled = centsEqual(settledBill.amountPaid, bill.amountPaid + payment.payment.amount);

    return (
      <div className="space-y-4">
        <StepFacts
          facts={[
            { label: 'Payment', value: payment.payment.paymentNumber },
            {
              label: 'Status',
              value: <StatusPill status={formatLabel(payment.payment.status)} tone={toneFor(payment.payment.status)} />,
            },
            { label: 'Provider answered', value: payment.payment.outcome ? formatLabel(payment.payment.outcome) : null },
            { label: 'Method', value: formatLabel(payment.payment.method) },
            { label: 'Amount', value: formatMoney(payment.payment.amount), numeric: true },
            { label: 'Provider reference', value: payment.payment.providerReference },
          ]}
        />

        <div className="border-border border-t pt-4">
          <p className="text-muted mb-3 text-[13px] font-medium">
            The bill, after Billing’s consumer had the fact
          </p>
          <StepFacts
            facts={[
              { label: 'Balance before', value: formatMoney(payment.balanceBefore), numeric: true },
              { label: 'Paid', value: formatMoney(settledBill.amountPaid), numeric: true },
              { label: 'Balance now', value: formatMoney(settledBill.balance), numeric: true },
              {
                label: 'Status',
                value: <StatusPill status={formatLabel(settledBill.status)} tone={toneFor(settledBill.status)} />,
              },
              { label: 'Settled', value: settledBill.paidAt ? formatDateTime(settledBill.paidAt) : null },
              {
                label: 'Printed total',
                value: formatMoney(settledBill.totalAmount),
                numeric: true,
              },
            ]}
          />

          {payment.approved && !settled && (
            <p className="text-warning mt-3 text-[13px]">
              The approval has not reached Billing’s consumer yet. The bill will move when the broker
              delivers it — nothing is lost, the fact is in the outbox.
            </p>
          )}

          {!payment.approved && (
            <p className="text-body mt-3 text-[13px]">
              Nothing was published, so nothing moved: the bill is still owed and the books will say
              so. The attempt is recorded and audited all the same.
            </p>
          )}
        </div>
      </div>
    );
  }

  return (
    <form onSubmit={form.handleSubmit((values) => take.mutate(values))} noValidate>
      <StepFields>
        <StepField label="Method" htmlFor="payment-method">
          <Select id="payment-method" fullWidth className="h-10 w-full text-sm" {...form.register('method')}>
            {paymentMethods.map((option) => (
              <option key={option} value={option}>
                {paymentMethodLabels[option]}
              </option>
            ))}
          </Select>
        </StepField>

        <StepField
          label="Instrument"
          htmlFor="payment-instrument"
          error={form.formState.errors.instrument?.message}
          hint={
            method === 'cash'
              ? 'Ignored for cash — the money is in the drawer, and the sandbox never refuses one.'
              : 'The sandbox refuses a tail of 0002, 9995 or 0000, so a demonstration can show a refusal on purpose.'
          }
        >
          <Input
            id="payment-instrument"
            disabled={method === 'cash'}
            {...form.register('instrument')}
            aria-invalid={Boolean(form.formState.errors.instrument)}
          />
        </StepField>

        <StepField
          label="Amount"
          htmlFor="payment-amount"
          error={form.formState.errors.amount?.message}
          hint={`Outstanding on ${bill.billNumber}: ${formatMoney(bill.balance)}`}
        >
          <Input
            id="payment-amount"
            type="number"
            step="0.01"
            min="0"
            {...form.register('amount')}
            aria-invalid={Boolean(form.formState.errors.amount)}
          />
        </StepField>
      </StepFields>

      <Button type="submit" className="mt-5" disabled={take.isPending}>
        {take.isPending ? 'Taking the payment…' : 'Take the payment'}
      </Button>
    </form>
  );
}

/**
 * Re-reads the bill until the amount paid moves, or the ceiling passes.
 *
 * Polling rather than sleeping a fixed span: it returns the instant the consumer commits, so a
 * fast delivery costs nothing. Returning the unmoved bill on a timeout rather than throwing is
 * deliberate — the payment succeeded, the fact is in the outbox, and telling somebody their money
 * vanished because a broker was slow would be a lie.
 */
async function awaitSettlement(
  billId: string,
  amountPaidBefore: number,
  deadline: number = Date.now() + settlementTimeoutMs,
): Promise<Bill> {
  const latest = await billingApi.get(billId);

  if (!centsEqual(latest.amountPaid, amountPaidBefore) || Date.now() >= deadline) {
    return latest;
  }

  await new Promise((resolve) => setTimeout(resolve, pollIntervalMs));

  return awaitSettlement(billId, amountPaidBefore, deadline);
}
