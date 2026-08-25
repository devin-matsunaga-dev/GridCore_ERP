import { useEffect } from 'react';
import { Check, TriangleAlert } from 'lucide-react';
import type { Bill } from '@/api/billing';
import type { ServiceAccount } from '@/api/customers';
import { useJournalEntries, useReceivables, useTrialBalance, type JournalEntry } from '@/api/finance';
import { ErrorState } from '@/components/registry/error-state';
import { Skeleton } from '@/components/ui/skeleton';
import { formatDate, formatMoney } from '@/lib/format';
import { cn } from '@/lib/utils';
import { expectedPostingSources, hasPosted, reconcile, type RevenueCycleState } from '../revenue-cycle';
import { StepFacts } from './step-card';

/**
 * SPEC step 9 — Generate Accounting Entries.
 *
 * Nothing is pressed here, and that is the point: Finance was never asked to post anything. It
 * heard `BillIssued` and `PaymentApproved` on the bus and raised both entries itself, from facts
 * alone, in a module that never calls back into the ones upstream of it. So this step waits and
 * then reads.
 *
 * What it reads is the reconciliation: three figures Billing states and the ledger arrives at
 * independently. Agreement is two modules reaching the same answer, not one copying the other's
 * total — which is why the check is worth showing at all.
 */

/** How often to re-ask while the postings are still in flight. */
const pollIntervalMs = 1_000;

export function AccountingStep({
  account,
  bill,
  state,
  onPosted,
}: {
  account: ServiceAccount;
  /** The bill as it now stands — settled if the payment was approved. */
  bill: Bill;
  state: RevenueCycleState;
  onPosted: (entries: JournalEntry[]) => void;
}) {
  const expected = expectedPostingSources(state);

  const entries = useJournalEntries({ serviceAccountId: account.id });
  const receivables = useReceivables({ serviceAccountId: account.id });
  const trialBalance = useTrialBalance();

  const arrived = hasPosted(entries.data, expected);

  // Keep asking until everything the walk should have caused has landed. The entries are written by
  // a consumer, so there is no request whose response could have carried them.
  useEffect(() => {
    if (arrived) return;

    const timer = setInterval(() => {
      void entries.refetch();
      void receivables.refetch();
      void trialBalance.refetch();
    }, pollIntervalMs);

    return () => clearInterval(timer);
  }, [arrived, entries, receivables, trialBalance]);

  // Report completion upward once, when the last expected posting has arrived.
  useEffect(() => {
    if (arrived && entries.data) onPosted(entries.data);
  }, [arrived, entries.data, onPosted]);

  const error = entries.error ?? receivables.error ?? trialBalance.error;

  if (error) {
    return (
      <ErrorState
        error={error}
        onRetry={() => {
          void entries.refetch();
          void receivables.refetch();
          void trialBalance.refetch();
        }}
      />
    );
  }

  if (!entries.data || !receivables.data || !trialBalance.data) {
    return <Skeleton className="h-40 w-full" />;
  }

  if (!arrived) {
    return (
      <div className="space-y-3">
        <p className="text-body text-[13px]">
          Waiting for the ledger. The facts are in the outbox and the broker is carrying them to
          Finance’s consumers — the entries appear here the moment they are posted.
        </p>
        <Skeleton className="h-24 w-full" />
      </div>
    );
  }

  const reconciliation = reconcile(bill, receivables.data, trialBalance.data);

  return (
    <div className="space-y-5">
      <div className="space-y-3">
        {entries.data.map((entry) => (
          <JournalEntryCard key={entry.id} entry={entry} />
        ))}
      </div>

      <div className="border-border border-t pt-4">
        <p className="text-muted mb-3 text-[13px] font-medium">
          Do the books agree with the bill?
        </p>

        <table className="w-full text-sm">
          <caption className="sr-only">
            What the billing register says against what the general ledger says
          </caption>
          <thead>
            <tr className="text-muted border-border border-b text-left text-[13px] font-medium">
              <th scope="col" className="py-2 pr-3 font-medium">
                Figure
              </th>
              <th scope="col" className="py-2 pr-3 text-right font-medium">
                Billing
              </th>
              <th scope="col" className="py-2 pr-3 text-right font-medium">
                Ledger
              </th>
              <th scope="col" className="py-2 text-right font-medium">
                Agrees
              </th>
            </tr>
          </thead>
          <tbody>
            {reconciliation.checks.map((check) => (
              <tr key={check.id} className="border-border/70 border-b last:border-0">
                <td className="py-2 pr-3">
                  <span className="text-heading block font-medium">{check.label}</span>
                  <span className="text-muted block text-xs">{check.note}</span>
                </td>
                <td className="tabular text-body py-2 pr-3 text-right align-top">{formatMoney(check.billing)}</td>
                <td className="tabular text-body py-2 pr-3 text-right align-top">{formatMoney(check.finance)}</td>
                <td className="py-2 text-right align-top">
                  <Agreement agrees={check.agrees} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>

        <div
          className={cn(
            'mt-4 flex items-start gap-3 rounded-card p-4',
            reconciliation.reconciles ? 'bg-success-soft' : 'bg-danger-soft',
          )}
          role="status"
        >
          {reconciliation.reconciles ? (
            <Check className="text-success mt-0.5 size-5 shrink-0" strokeWidth={2} aria-hidden="true" />
          ) : (
            <TriangleAlert className="text-danger mt-0.5 size-5 shrink-0" strokeWidth={2} aria-hidden="true" />
          )}
          <div className="min-w-0">
            <p className={cn('text-sm font-semibold', reconciliation.reconciles ? 'text-success' : 'text-danger')}>
              {reconciliation.reconciles ? 'The numbers reconcile' : 'The numbers do not reconcile'}
            </p>
            <p className="text-body mt-1 text-[13px]">
              {reconciliation.ledgerBalances
                ? `Debits and credits across the whole ledger are equal — ${formatMoney(trialBalance.data.totalDebits)} against ${formatMoney(trialBalance.data.totalCredits)}.`
                : `The trial balance is out by ${formatMoney(trialBalance.data.difference)}, which no posting GridCore makes can cause.`}
            </p>
          </div>
        </div>
      </div>

      <div className="border-border border-t pt-4">
        <p className="text-muted mb-3 text-[13px] font-medium">The accounts this walk moved</p>
        <StepFacts
          facts={trialBalance.data.rows
            .filter((row) => row.lineCount > 0)
            .map((row) => ({
              label: `${row.accountCode} ${row.accountName}`,
              value: formatMoney(row.balance),
              numeric: true,
            }))}
        />
      </div>
    </div>
  );
}

function JournalEntryCard({ entry }: { entry: JournalEntry }) {
  return (
    <div className="border-border rounded-card border p-4">
      <div className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
        <p className="text-heading text-sm font-semibold">
          {entry.entryNumber} — {entry.description}
        </p>
        <p className="text-muted text-xs">
          {/* The accounting date is the event's own, never the clock's. */}
          Posted on {formatDate(entry.postedOn)} · {entry.source} · by {entry.actorId}
        </p>
      </div>

      <table className="mt-3 w-full text-sm">
        <caption className="sr-only">The lines of journal entry {entry.entryNumber}</caption>
        <thead>
          <tr className="text-muted border-border border-b text-left text-[13px] font-medium">
            <th scope="col" className="py-1.5 pr-3 font-medium">
              Account
            </th>
            <th scope="col" className="py-1.5 pr-3 text-right font-medium">
              Debit
            </th>
            <th scope="col" className="py-1.5 text-right font-medium">
              Credit
            </th>
          </tr>
        </thead>
        <tbody>
          {entry.lines.map((line) => (
            <tr key={line.sequence} className="border-border/70 border-b last:border-0">
              <td className="text-body py-1.5 pr-3">
                <span className="text-muted tabular mr-2 text-xs">{line.accountCode}</span>
                {line.accountName}
              </td>
              {/* The magnitude always goes on the correct side — never a negative on the other one. */}
              <td className="tabular text-heading py-1.5 pr-3 text-right">
                {line.debit === 0 ? '' : formatMoney(line.debit)}
              </td>
              <td className="tabular text-heading py-1.5 text-right">
                {line.credit === 0 ? '' : formatMoney(line.credit)}
              </td>
            </tr>
          ))}
        </tbody>
        <tfoot>
          <tr className="border-border border-t">
            <th scope="row" className="text-heading py-1.5 pr-3 text-right text-[13px] font-semibold">
              Totals
            </th>
            <td className="tabular text-heading py-1.5 pr-3 text-right font-semibold">
              {formatMoney(entry.totalDebits)}
            </td>
            <td className="tabular text-heading py-1.5 text-right font-semibold">
              {formatMoney(entry.totalCredits)}
            </td>
          </tr>
        </tfoot>
      </table>
    </div>
  );
}

function Agreement({ agrees }: { agrees: boolean }) {
  return agrees ? (
    <span className="text-success inline-flex items-center gap-1 text-[13px] font-medium">
      <Check className="size-4" strokeWidth={2.5} aria-hidden="true" />
      Yes
    </span>
  ) : (
    <span className="text-danger inline-flex items-center gap-1 text-[13px] font-medium">
      <TriangleAlert className="size-4" strokeWidth={2.5} aria-hidden="true" />
      No
    </span>
  );
}
