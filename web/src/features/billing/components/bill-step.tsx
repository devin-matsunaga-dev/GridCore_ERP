import { useMutation } from '@tanstack/react-query';
import { billingApi, type Bill, type BillingRun } from '@/api/billing';
import type { ServiceAccount } from '@/api/customers';
import { toast } from '@/components/feedback/toast';
import { Button } from '@/components/ui/button';
import { StatusPill } from '@/components/ui/status';
import { formatCount, formatDate, formatLabel, formatMoney, formatQuantity } from '@/lib/format';
import { StepFacts } from './step-card';

/**
 * SPEC step 6 — Generate Bill.
 *
 * Two acts behind one button, and the difference between them matters more than any other pair in
 * this walk: a **run** prices the cycle and produces drafts, publishing nothing; **issuing** is
 * what makes a bill money the utility is owed, and it is what publishes `BillIssued` and so what
 * reaches Finance. The facts report both, so a reader can see that the ledger filled because a
 * bill was issued and not because one was calculated.
 *
 * The run skips readings it should not bill and says why in words. Those reasons are shown: a
 * demonstration in which a billing run silently billed everything would be hiding the part a
 * billing officer actually spends their day on.
 */

export type BillStepResult = { billingRun: BillingRun; bill: Bill };

export function BillStep({
  account,
  cycleCode,
  result,
  onDone,
}: {
  account: ServiceAccount;
  cycleCode: string;
  result?: BillStepResult;
  onDone: (result: BillStepResult) => void;
}) {
  const raise = useMutation({
    mutationFn: async () => {
      const billingRun = await billingApi.run(cycleCode);
      const draft = billingRun.bills.find((candidate) => candidate.serviceAccountId === account.id);

      if (!draft) {
        // The run reports why it passed a reading over; surface that rather than "no bill found".
        const skipped = billingRun.skipped.find((row) => row.serviceLocationId === account.serviceLocationId);

        throw new Error(
          skipped
            ? `The run did not bill this premise: ${skipped.reason}.`
            : `The run raised no bill for account ${account.accountNumber}.`,
        );
      }

      const bill = await billingApi.issue(draft.id);

      return { billingRun, bill };
    },
    onSuccess: (raised) => {
      toast.success(
        `Bill ${raised.bill.billNumber} issued`,
        `${formatMoney(raised.bill.totalAmount)} · ${formatCount(raised.billingRun.raised)} raised across the cycle`,
      );
      onDone(raised);
    },
    onError: (error) => toast.apiError(error, 'The bill could not be raised.'),
  });

  if (result) {
    const { billingRun, bill } = result;

    return (
      <div className="space-y-4">
        <StepFacts
          facts={[
            { label: 'Bills raised', value: formatCount(billingRun.raised), numeric: true },
            { label: 'Billed across the cycle', value: formatMoney(billingRun.totalBilled), numeric: true },
            { label: 'Readings skipped', value: formatCount(billingRun.skippedCount), numeric: true },
          ]}
        />

        {billingRun.skippedCount > 0 && (
          <ul className="text-muted space-y-1 text-[13px]">
            {Object.entries(billingRun.byReason).map(([reason, count]) => (
              <li key={reason}>
                <span className="tabular text-heading font-medium">{formatCount(count)}</span> — {reason}
              </li>
            ))}
          </ul>
        )}

        <div className="border-border border-t pt-4">
          <p className="text-muted mb-3 text-[13px] font-medium">This account’s bill</p>
          <StepFacts
            facts={[
              { label: 'Bill number', value: bill.billNumber },
              { label: 'Status', value: <StatusPill status={formatLabel(bill.status)} /> },
              { label: 'Tariff', value: `${bill.ratePlanCode} — ${bill.ratePlanName}` },
              {
                label: 'Period',
                value: `${formatDate(bill.periodStart)} – ${formatDate(bill.periodEnd)}`,
              },
              {
                label: 'Consumption',
                value: `${formatQuantity(bill.consumption)} ${bill.unitOfMeasure}`,
                numeric: true,
              },
              { label: 'Due', value: bill.dueDate ? formatDate(bill.dueDate) : null },
            ]}
          />

          <table className="mt-4 w-full text-sm">
            <caption className="sr-only">The lines of bill {bill.billNumber}</caption>
            <thead>
              <tr className="text-muted border-border border-b text-left text-[13px] font-medium">
                <th scope="col" className="py-2 pr-3 font-medium">
                  Line
                </th>
                <th scope="col" className="py-2 pr-3 text-right font-medium">
                  Units
                </th>
                <th scope="col" className="py-2 pr-3 text-right font-medium">
                  Rate
                </th>
                <th scope="col" className="py-2 text-right font-medium">
                  Amount
                </th>
              </tr>
            </thead>
            <tbody>
              {bill.lines.map((line) => (
                <tr key={line.sequence} className="border-border/70 border-b last:border-0">
                  <td className="text-body py-2 pr-3">{line.description}</td>
                  <td className="tabular text-body py-2 pr-3 text-right">
                    {line.units === null ? '—' : formatQuantity(line.units)}
                  </td>
                  <td className="tabular text-body py-2 pr-3 text-right">
                    {line.ratePerUnit === null ? '—' : line.ratePerUnit.toFixed(4)}
                  </td>
                  <td className="tabular text-heading py-2 text-right">{formatMoney(line.amount)}</td>
                </tr>
              ))}
            </tbody>
            <tfoot>
              <tr className="border-border border-t">
                {/* The printed total is the sum of the printed lines — the first thing a customer
                    checks by hand, and something the aggregate asserts rather than assumes. */}
                <th scope="row" colSpan={3} className="text-heading py-2 pr-3 text-right font-semibold">
                  Total
                </th>
                <td className="tabular text-heading py-2 text-right font-semibold">
                  {formatMoney(bill.totalAmount)}
                </td>
              </tr>
            </tfoot>
          </table>
        </div>
      </div>
    );
  }

  return (
    <Button onClick={() => raise.mutate()} disabled={raise.isPending}>
      {raise.isPending ? 'Billing the cycle…' : 'Run billing and issue the bill'}
    </Button>
  );
}
