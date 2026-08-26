import { zodResolver } from '@hookform/resolvers/zod';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { PiggyBank } from 'lucide-react';
import { useCallback, useState } from 'react';
import { useForm, useWatch, type Resolver } from 'react-hook-form';
import { z } from 'zod';
import type { Bill } from '@/api/billing';
import {
  customerKeys,
  customersApi,
  serviceTypeLabel,
  type DepositEntry,
  type DepositLedger,
  type DepositRequirement,
} from '@/api/customers';
import { toast } from '@/components/feedback/toast';
import type { Column } from '@/components/registry/data-table';
import { EmptyState } from '@/components/registry/empty-state';
import { RegistryTableCard } from '@/components/registry/registry-table-card';
import { useTableState } from '@/components/registry/table-state';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import { StatusPill } from '@/components/ui/status';
import { ErrorState } from '@/components/registry/error-state';
import { formatDate, formatMoney } from '@/lib/format';
import { isWholeCents, toCents } from '@/lib/money';
import { IntakeField, IntakeFields } from '../registration/components/intake-field';
import {
  applicableAmount,
  billsADepositCouldSettle,
  depositBasisLabel,
  depositKindLabel,
  depositKindTone,
  depositStanding,
  depositStandingLabel,
  depositStandingTone,
  sortDepositEntries,
} from '../deposits';

/**
 * The customer's security deposit: what is held, and every movement behind it.
 *
 * **A ledger, not a field.** The figure at the top is `Customer.depositHeld`, which since WP-2.12 is
 * the projection of the rows below it — there is no "edit deposit" here and there is deliberately
 * none anywhere else, because a balance a form could type over is a balance that disagrees with the
 * general ledger the first time somebody does. The three buttons are movements: they add an entry
 * and the balance follows.
 *
 * A table, because it is a register of like rows — the owner's rule for this page, and the same
 * shape the bills, payments, contacts and service accounts all take.
 */
export function CustomerDepositCard({
  customerId,
  ledger,
  bills,
  isLoading,
  error,
  onRetry,
}: {
  customerId: string;
  ledger: DepositLedger | undefined;
  bills: readonly Bill[];
  isLoading: boolean;
  error: unknown;
  onRetry: () => void;
}) {
  const [act, setAct] = useState<DepositAct | null>(null);

  const entries = sortDepositEntries(ledger?.entries ?? []);
  const table = useTableState({ rows: entries, columns: depositColumns });

  const settleable = billsADepositCouldSettle(bills);
  const held = ledger?.balance ?? 0;

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader>
          <CardTitle>Security deposit</CardTitle>
        </CardHeader>
        <CardContent className="space-y-5">
          {error ? (
            <ErrorState error={error} onRetry={onRetry} />
          ) : isLoading || !ledger ? (
            <div className="space-y-3">
              <Skeleton className="h-9 w-40" />
              <Skeleton className="h-4 w-64" />
            </div>
          ) : (
            <DepositStandingRow ledger={ledger} />
          )}

          {ledger && !error && (
            <div className="flex flex-wrap gap-2">
              <Button size="sm" onClick={() => setAct(act === 'collect' ? null : 'collect')}>
                {act === 'collect' ? 'Cancel' : 'Collect'}
              </Button>

              {/*
                Both of these need money to move, so both are disabled at a zero balance — and the
                title says which of the two reasons it is. A button that 409s on click is a button
                that made the rep find out the hard way.
              */}
              <Button
                variant="secondary"
                size="sm"
                disabled={toCents(held) === 0 || settleable.length === 0}
                title={
                  toCents(held) === 0
                    ? 'Nothing is held against this customer.'
                    : settleable.length === 0
                      ? 'This customer has no outstanding bill to settle.'
                      : undefined
                }
                onClick={() => setAct(act === 'apply' ? null : 'apply')}
              >
                {act === 'apply' ? 'Cancel' : 'Apply to a bill'}
              </Button>

              <Button
                variant="secondary"
                size="sm"
                disabled={toCents(held) === 0}
                title={toCents(held) === 0 ? 'Nothing is held against this customer.' : undefined}
                onClick={() => setAct(act === 'refund' ? null : 'refund')}
              >
                {act === 'refund' ? 'Cancel' : 'Refund'}
              </Button>
            </div>
          )}

          {ledger && act && (
            <div className="border-border border-t pt-5">
              <DepositActForm
                act={act}
                customerId={customerId}
                ledger={ledger}
                bills={settleable}
                onDone={() => setAct(null)}
              />
            </div>
          )}
        </CardContent>
      </Card>

      {ledger && ledger.requirement.accounts.length > 0 && (
        <DepositRequirementCard requirement={ledger.requirement} />
      )}

      <div className="space-y-4">
        <h3 className="text-heading text-lg font-semibold">Movements</h3>

        <RegistryTableCard
          columns={depositColumns}
          table={table}
          rowKey={(entry) => entry.id}
          label="Deposit movements"
          isLoading={isLoading}
          error={error}
          onRetry={onRetry}
          returnedRows={entries.length}
          empty={
            <EmptyState
              icon={PiggyBank}
              title="No deposit movements"
              message="Nothing has been taken from this customer as a security deposit. Collecting one records an entry here and posts the liability to the ledger."
            />
          }
        />
      </div>
    </div>
  );
}

/**
 * What the schedule asks, one line per open account (WP-2.17).
 *
 * Its own card rather than a line in the panel above, because a customer taking three supplies is
 * assessed three times and one figure could only ever describe one of them. A register of like rows
 * is a table (WP-2.10's rule) — but a customer holds at most three, so this is a definition list
 * rather than a data table with a header, a sort and a paging window it will never use.
 *
 * The BASIS is the column that earns its place: "two months of your average usage" and "the
 * published minimum" are what a rep reads out, and the figure alone cannot be argued with.
 */
function DepositRequirementCard({ requirement }: { requirement: DepositRequirement }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>What the schedule asks</CardTitle>
        <p className="text-muted mt-1 text-[13px]">
          One line per open account. {requirement.customerClass} rates, assessed against what each premise uses.
        </p>
      </CardHeader>

      <CardContent className="space-y-0 pt-0">
        {requirement.accounts.map((line) => (
          <div
            key={line.serviceAccountId}
            className="border-border flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1 border-t py-3 first:border-t-0"
          >
            <div className="min-w-0">
              <p className="text-body text-sm font-medium">
                {serviceTypeLabel(line.serviceType)}
                <span className="text-muted ml-2 text-[13px] whitespace-nowrap">{line.accountNumber}</span>
              </p>
              <p className="text-muted mt-0.5 text-[13px]">{depositBasisLabel(line)}</p>
            </div>

            <p className="tabular text-heading text-sm font-semibold">{formatMoney(line.requiredAmount)}</p>
          </div>
        ))}

        <div className="border-border flex items-baseline justify-between gap-4 border-t pt-3">
          <p className="text-heading text-sm font-semibold">Total required</p>
          <p className="tabular text-heading text-sm font-bold">{formatMoney(requirement.requiredAmount)}</p>
        </div>
      </CardContent>
    </Card>
  );
}

/** How many accounts a total is spread across, in words rather than a bare digit. */
function accountsPhrase(count: number): string {
  return count === 1 ? '1 account' : `${count} accounts`;
}

/** The balance, what the schedule asks, and whether the two agree. */
function DepositStandingRow({ ledger }: { ledger: DepositLedger }) {
  const standing = depositStanding(ledger);

  return (
    <div className="flex flex-wrap items-end justify-between gap-4">
      <div>
        <p className="text-muted text-[13px] font-medium">Held on account</p>
        <p className="tabular text-heading mt-1.5 text-[30px] leading-none font-bold">
          {formatMoney(ledger.balance)}
        </p>
        <p className="text-muted mt-2 text-[13px]">
          {/*
            The schedule is what a rep quotes on the telephone, so it is on screen beside the
            balance rather than a click away. Since WP-2.17 it is a SUM over the supplies this
            customer takes, which is why the line says how many accounts are behind it. The shortfall
            comes from the host already floored at zero — a customer holding more than the schedule
            asks is not short by a negative amount.
          */}
          {ledger.requirement.accounts.length === 0
            ? 'No open account — the schedule asks nothing until a supply is taken'
            : `Schedule asks ${formatMoney(ledger.requirement.requiredAmount)} across ${accountsPhrase(ledger.requirement.accounts.length)}`}
          {toCents(ledger.requirement.shortfallAmount) > 0 && (
            <> · {formatMoney(ledger.requirement.shortfallAmount)} short</>
          )}
        </p>
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <StatusPill status={depositStandingLabel(standing)} tone={depositStandingTone(standing)} />

        {/*
          Stored, never accrued — the MVP records the terms the money was taken under and computes
          nothing from them. Shown only when it is true, so the ordinary case says nothing at all.
        */}
        {ledger.isInterestBearing && <StatusPill status="Interest-bearing" tone="info" />}
      </div>
    </div>
  );
}

const depositColumns: Column<DepositEntry>[] = [
  {
    key: 'recordedAt',
    header: 'When',
    sortValue: (entry) => entry.recordedAt,
    cell: (entry) => <span className="text-body text-[13px] whitespace-nowrap">{formatDate(entry.recordedAt)}</span>,
  },
  {
    key: 'kind',
    header: 'Movement',
    primary: true,
    sortValue: (entry) => entry.kind,
    cell: (entry) => <StatusPill status={depositKindLabel(entry.kind)} tone={depositKindTone(entry.kind)} />,
  },
  {
    key: 'amount',
    header: 'Amount',
    align: 'right',
    // Sorted and shown SIGNED, though the stored amount never is: a column a rep scans for "where
    // did the deposit go" has to put money out below money in, and the kind carrying the direction
    // is a fact about the ledger rather than about this column.
    sortValue: (entry) => entry.signedAmount,
    cell: (entry) => (
      <span className={entry.signedAmount < 0 ? 'text-body tabular' : 'text-success tabular font-medium'}>
        {entry.signedAmount < 0 ? '−' : '+'}
        {formatMoney(entry.amount)}
      </span>
    ),
  },
  {
    key: 'balanceAfter',
    header: 'Balance after',
    align: 'right',
    sortValue: (entry) => entry.balanceAfter,
    cell: (entry) => <span className="tabular text-heading font-medium">{formatMoney(entry.balanceAfter)}</span>,
  },
  {
    key: 'billNumber',
    header: 'Bill',
    // Its own column and never wrapping — the owner's rule from WP-2.10. A registry number broken
    // across two lines reads as two numbers.
    sortValue: (entry) => entry.billNumber,
    cell: (entry) =>
      entry.billNumber ? (
        <span className="tabular text-body text-[13px] whitespace-nowrap">{entry.billNumber}</span>
      ) : (
        <span className="text-muted">—</span>
      ),
  },
  {
    key: 'reason',
    header: 'Reason',
    wide: true,
    sortValue: (entry) => entry.reason,
    cell: (entry) => <span className="text-body text-[13px]">{entry.reason ?? <span className="text-muted">—</span>}</span>,
  },
  {
    key: 'actor',
    header: 'By',
    sortValue: (entry) => entry.actorName ?? entry.actorId,
    cell: (entry) => <span className="text-muted text-[13px]">{entry.actorName ?? entry.actorId}</span>,
  },
];

type DepositAct = 'collect' | 'apply' | 'refund';

const actLabels: Record<DepositAct, { title: string; submit: string; pending: string }> = {
  collect: { title: 'Collect a deposit', submit: 'Collect', pending: 'Collecting…' },
  apply: { title: 'Apply the deposit to a bill', submit: 'Apply', pending: 'Applying…' },
  refund: { title: 'Refund the deposit', submit: 'Refund', pending: 'Refunding…' },
};

type DepositFormValues = { amount: string; billId: string; reason: string; isInterestBearing: boolean };

/**
 * One movement's form.
 *
 * **The browser refuses what the host would refuse, deliberately duplicating the rules** — WP-2.8's
 * call, for the same reason: the host stays the authority, and the duplication buys the rep the
 * answer at the moment it becomes wrong rather than as a 409 after they have pressed the button.
 * The schema is built per validation from the values being validated, because the ceilings depend
 * on the balance held and on which bill was chosen, both of which move while the form is open.
 */
function DepositActForm({
  act,
  customerId,
  ledger,
  bills,
  onDone,
}: {
  act: DepositAct;
  customerId: string;
  ledger: DepositLedger;
  bills: readonly Bill[];
  onDone: () => void;
}) {
  const queryClient = useQueryClient();
  const labels = actLabels[act];

  // Built per validation from the values being validated: the ceilings depend on the balance held
  // and on which bill was chosen, and both move while the form is open. The same call
  // `customer-registration-page.tsx` made, for the same reason.
  const resolver = useCallback<Resolver<DepositFormValues>>(
    (values, context, options) =>
      zodResolver(depositActSchema(act, ledger, bills, values))(values, context, options),
    [act, ledger, bills],
  );

  const form = useForm<DepositFormValues>({
    resolver,
    defaultValues: {
      amount: '',
      billId: bills[0]?.id ?? '',
      reason: '',
      isInterestBearing: ledger.isInterestBearing,
    },
    mode: 'onTouched',
  });

  const move = useMutation({
    mutationFn: (values: DepositFormValues) => {
      const amount = Number(values.amount);
      const reason = values.reason.trim() || null;

      switch (act) {
        case 'collect':
          return customersApi.collectDeposit(customerId, {
            amount,
            isInterestBearing: values.isInterestBearing,
            reason,
          });
        case 'apply':
          return customersApi.applyDeposit(customerId, { billId: values.billId, amount, reason });
        default:
          return customersApi.refundDeposit(customerId, { amount, reason });
      }
    },
    onSuccess: (entry) => {
      toast.success(
        `${depositKindLabel(entry.kind)} — ${formatMoney(entry.amount)}`,
        `${formatMoney(entry.balanceAfter)} now held on account.`,
      );

      // The ledger and the customer both move, and applying moves a bill as well — through a
      // consumer, so the bill's own figures may lag a moment. Invalidating all three is what stops
      // the header quoting one balance while the tab below quotes another.
      void queryClient.invalidateQueries({ queryKey: customerKeys.deposits(customerId) });
      void queryClient.invalidateQueries({ queryKey: customerKeys.detail(customerId) });
      void queryClient.invalidateQueries({ queryKey: ['bills'] });

      onDone();
    },
    onError: (error) => toast.apiError(error, 'The deposit could not be moved.'),
  });

  const { errors } = form.formState;

  // `useWatch` rather than `form.watch()`: the latter returns a new value on every render, which
  // React Compiler cannot memoize past.
  const billId = useWatch({ control: form.control, name: 'billId' });
  const chosen = bills.find((bill) => bill.id === billId);

  return (
    <form className="space-y-4" onSubmit={form.handleSubmit((values) => move.mutate(values))}>
      <p className="text-heading text-[15px] font-semibold">{labels.title}</p>

      <IntakeFields>
        {act === 'apply' && (
          <IntakeField
            label="Bill"
            htmlFor="deposit-bill"
            error={errors.billId?.message}
            hint={chosen ? `${formatMoney(chosen.balance)} outstanding` : undefined}
          >
            <Select id="deposit-bill" fullWidth {...form.register('billId')}>
              {bills.map((bill) => (
                <option key={bill.id} value={bill.id}>
                  {bill.billNumber} — {formatMoney(bill.balance)} outstanding
                </option>
              ))}
            </Select>
          </IntakeField>
        )}

        <IntakeField
          label="Amount"
          htmlFor="deposit-amount"
          error={errors.amount?.message}
          hint={ceilingHint(act, ledger, chosen)}
        >
          <Input id="deposit-amount" inputMode="decimal" {...form.register('amount')} aria-invalid={Boolean(errors.amount)} />
        </IntakeField>

        <IntakeField label="Reason" htmlFor="deposit-reason" error={errors.reason?.message}>
          <Input id="deposit-reason" {...form.register('reason')} />
        </IntakeField>
      </IntakeFields>

      {act === 'collect' && (
        <div className="flex items-start gap-2.5">
          <input
            id="deposit-interest"
            type="checkbox"
            className="border-border text-primary focus-visible:ring-primary/40 mt-0.5 size-4 shrink-0 rounded-[4px] focus-visible:ring-2 focus-visible:outline-none"
            {...form.register('isInterestBearing')}
          />
          <div className="min-w-0">
            <label htmlFor="deposit-interest" className="text-body text-[13px]">
              Interest-bearing
            </label>
            <p className="text-muted mt-0.5 text-xs">
              Recorded against the holding. Nothing accrues on it in this release.
            </p>
          </div>
        </div>
      )}

      <div className="flex justify-end gap-2">
        <Button type="button" variant="secondary" onClick={onDone} disabled={move.isPending}>
          Cancel
        </Button>
        <Button type="submit" disabled={move.isPending}>
          {move.isPending ? labels.pending : labels.submit}
        </Button>
      </div>
    </form>
  );
}

/** What the amount field says the ceiling is, in the words a rep would use. */
function ceilingHint(act: DepositAct, ledger: DepositLedger, bill: Bill | undefined): string | undefined {
  switch (act) {
    case 'collect':
      return toCents(ledger.requirement.shortfallAmount) > 0
        ? `${formatMoney(ledger.requirement.shortfallAmount)} would bring this customer up to the schedule.`
        : 'The schedule is already covered; more may still be taken.';
    case 'apply':
      return bill
        ? `Up to ${formatMoney(applicableAmount(ledger.balance, bill.balance))} — whichever of the deposit and the bill runs out first.`
        : undefined;
    default:
      return `Up to ${formatMoney(ledger.balance)} is held.`;
  }
}

/**
 * The rules, built from the values being validated.
 *
 * Every one of them is also enforced by the host — this is not the authority, it is the answer
 * arriving before the request does. The two ceilings differ by act because the host's do: a
 * collection has none (WP-2.8's schedule cap belongs to the intake), a refund is capped by the
 * balance held, and an application by whichever of the balance and the bill runs out first.
 */
function depositActSchema(
  act: DepositAct,
  ledger: DepositLedger,
  bills: readonly Bill[],
  values: DepositFormValues,
) {
  const bill = bills.find((candidate) => candidate.id === values.billId);

  const ceiling =
    act === 'refund'
      ? ledger.balance
      : act === 'apply' && bill
        ? applicableAmount(ledger.balance, bill.balance)
        : undefined;

  return z.object({
    billId: act === 'apply' ? z.string().min(1, 'Choose the bill to settle.') : z.string(),

    amount: z
      .string()
      .min(1, 'Enter an amount.')
      .refine((raw) => Number.isFinite(Number(raw)), 'Enter an amount.')
      .refine((raw) => Number(raw) > 0, 'A movement of nothing is not a movement.')
      .refine((raw) => isWholeCents(Number(raw)), 'Amounts are to the cent.')
      .refine(
        (raw) => ceiling === undefined || toCents(Number(raw)) <= toCents(ceiling),
        ceiling === undefined
          ? ''
          : act === 'refund'
            ? `Only ${formatMoney(ceiling)} is held.`
            : `Only ${formatMoney(ceiling)} can go against this bill.`,
      ),

    reason: z.string().max(1024, 'Shorten the reason.'),
    isInterestBearing: z.boolean(),
  });
}
