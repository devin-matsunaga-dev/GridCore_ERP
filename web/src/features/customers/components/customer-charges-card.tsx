import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Receipt } from 'lucide-react';
import { useMemo, useState } from 'react';
import {
  billKeys,
  feeCodes,
  feeKeys,
  feesApi,
  useAccountCharges,
  useFeeSchedule,
  type AccountCharge,
  type FeeCode,
  type FeeScheduleEntry,
} from '@/api/billing';
import { type ServiceAccount } from '@/api/customers';
import { toast } from '@/components/feedback/toast';
import type { Column } from '@/components/registry/data-table';
import { EmptyState } from '@/components/registry/empty-state';
import { RegistryTableCard } from '@/components/registry/registry-table-card';
import { useTableState } from '@/components/registry/table-state';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { StatusPill } from '@/components/ui/status';
import { formatDate, formatMoney } from '@/lib/format';
import { IntakeField, IntakeFields } from '../registration/components/intake-field';
import { chargeStatusTone, chargeableAccounts, feeCodeLabel, isActionable, pendingTotal, priceOf, sortCharges } from '../charges';

/**
 * The fees raised against one customer, and the desk that raises one (WP-2.16's screen, shipped
 * with WP-2.18).
 *
 * **The catalogue is the utility's, not the form's.** Every figure comes from
 * `GET /api/fee-schedule` priced for today, and the charge stamps the schedule row that priced it —
 * so a fee raised this morning still reads $60 after the schedule moves to $75 next July. There is
 * no field for an amount, deliberately: a rep who could type one would be a rep inventing a
 * published charge.
 *
 * **The queries live in the card rather than at the page.** The 360's rule is that its queries live
 * at the page and switching tabs issues no request — right for the registers the summary is built
 * from, and wrong for these two: nothing outside this tab reads a fee, and a page that fetched the
 * whole catalogue on every open would pay for a tab most telephone calls never reach. The
 * documents tab is the other exception, for a different reason (its reads are audited).
 */
export function CustomerChargesCard({
  customerId,
  accounts,
}: {
  customerId: string;
  accounts: readonly ServiceAccount[];
}) {
  const charges = useAccountCharges({ customerId }, Boolean(customerId));
  const schedule = useFeeSchedule();
  const queryClient = useQueryClient();

  const rows = useMemo(() => sortCharges(charges.data ?? []), [charges.data]);
  const table = useTableState({ rows, columns: chargeColumns });
  const outstanding = useMemo(() => pendingTotal(rows), [rows]);

  function refresh() {
    void queryClient.invalidateQueries({ queryKey: ['account-charges'] });
    void queryClient.invalidateQueries({ queryKey: billKeys.all });
    void queryClient.invalidateQueries({ queryKey: feeKeys.schedule() });
  }

  const cancel = useMutation({
    mutationFn: ({ charge, reason }: { charge: AccountCharge; reason: string }) =>
      feesApi.cancel(charge.id, reason),
    onSuccess: (charge) => {
      toast.success(`${feeCodeLabel(charge.code)} withdrawn on ${charge.accountNumber}.`);
      refresh();
    },
    onError: (error) => toast.apiError(error, 'That charge could not be withdrawn.'),
  });

  const billNow = useMutation({
    mutationFn: (charge: AccountCharge) => feesApi.billNow(charge.id, 'Billed at the counter.'),
    onSuccess: (result) => {
      toast.success(
        `${feeCodeLabel(result.charge.code)} billed as ${result.bill.billNumber}.`,
        `${formatMoney(result.bill.totalAmount)} is payable now.`,
      );
      refresh();
    },
    onError: (error) => toast.apiError(error, 'That charge could not be billed.'),
  });

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader>
          <CardTitle>Charges</CardTitle>
        </CardHeader>
        <CardContent className="space-y-5">
          <div className="flex flex-wrap items-end justify-between gap-4">
            <div>
              <p className="text-muted text-[13px] font-medium">Raised and not yet billed</p>
              <p className="text-heading tabular mt-1.5 text-[30px] leading-none font-bold">
                {formatMoney(outstanding)}
              </p>
              <p className="text-muted mt-2 text-[13px]">
                {/*
                  A pending charge is NOT a receivable — Finance posts on BillIssued, so a fee
                  becomes money owed when it reaches a bill. Saying so here is what stops a rep
                  reading this figure as part of the balance above.
                */}
                A raised fee lands on the next cycle bill, or on a counter bill of its own. It is not
                a balance until it does.
              </p>
            </div>
          </div>

          <RaiseChargeForm
            customerId={customerId}
            accounts={accounts}
            schedule={schedule.data ?? []}
            isScheduleLoading={schedule.isPending}
            onRaised={refresh}
          />
        </CardContent>
      </Card>

      <RegistryTableCard
        label="Charges"
        columns={chargeColumns}
        table={table}
        rowKey={(row) => row.id}
        isLoading={charges.isPending}
        error={charges.isError ? charges.error : undefined}
        onRetry={() => void charges.refetch()}
        returnedRows={rows.length}
        empty={
          <EmptyState
            icon={Receipt}
            title="No fees raised"
            message="Connection charges, reconnection fees and penalties appear here as they are raised."
          />
        }
      />

      {rows.some(isActionable) && (
        <Card>
          <CardHeader>
            <CardTitle>Pending charges</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3">
            {rows.filter(isActionable).map((charge) => (
              <div
                key={charge.id}
                className="border-border flex flex-wrap items-center justify-between gap-3 rounded-card border px-4 py-3"
              >
                <span className="min-w-0">
                  <span className="text-heading block truncate text-[13px] font-medium">
                    {feeCodeLabel(charge.code)} — {formatMoney(charge.amount)}
                  </span>
                  <span className="text-muted block truncate text-xs">
                    {charge.accountNumber} · {charge.reason}
                  </span>
                </span>
                <span className="flex shrink-0 gap-2">
                  <Button
                    size="sm"
                    variant="secondary"
                    disabled={billNow.isPending}
                    onClick={() => billNow.mutate(charge)}
                  >
                    Bill at the counter
                  </Button>
                  <Button
                    size="sm"
                    variant="ghost"
                    disabled={cancel.isPending}
                    onClick={() => cancel.mutate({ charge, reason: 'Withdrawn by customer service.' })}
                  >
                    Withdraw
                  </Button>
                </span>
              </div>
            ))}
          </CardContent>
        </Card>
      )}
    </div>
  );
}

/** Pick an account, pick a published fee, say why. There is no amount field, and that is the point. */
function RaiseChargeForm({
  customerId,
  accounts,
  schedule,
  isScheduleLoading,
  onRaised,
}: {
  customerId: string;
  accounts: readonly ServiceAccount[];
  schedule: readonly FeeScheduleEntry[];
  isScheduleLoading: boolean;
  onRaised: () => void;
}) {
  const chargeable = useMemo(() => chargeableAccounts(accounts), [accounts]);

  const [serviceAccountId, setServiceAccountId] = useState('');
  const [code, setCode] = useState<FeeCode>('ServiceConnection');
  const [reason, setReason] = useState('');

  const priced = priceOf(schedule, code);
  const queryClient = useQueryClient();

  const raise = useMutation({
    mutationFn: () => feesApi.raise({ serviceAccountId, code, reason: reason.trim() }),
    onSuccess: (charge) => {
      toast.success(
        `${feeCodeLabel(charge.code)} raised on ${charge.accountNumber}.`,
        `${formatMoney(charge.amount)} will appear on the next bill.`,
      );
      setReason('');
      void queryClient.invalidateQueries({ queryKey: ['account-charges'] });
      onRaised();
    },
    onError: (error) => toast.apiError(error, 'That fee could not be raised.'),
  });

  if (chargeable.length === 0) {
    return (
      <p className="text-muted text-[13px]">
        A fee is raised against a service account, and this customer holds none yet. Approve their
        application first.
      </p>
    );
  }

  return (
    <div className="border-border rounded-card border p-4">
      <IntakeFields>
        <IntakeField label="Account" htmlFor="charge-account">
          <Select
            id="charge-account"
            fullWidth
            value={serviceAccountId}
            onChange={(event) => setServiceAccountId(event.target.value)}
          >
            <option value="">Choose an account…</option>
            {chargeable.map((account) => (
              <option key={account.id} value={account.id}>
                {account.accountNumber} · {account.status}
              </option>
            ))}
          </Select>
        </IntakeField>

        <IntakeField
          label="Fee"
          htmlFor="charge-code"
          hint={
            isScheduleLoading
              ? 'Reading the published schedule…'
              : priced
                ? `${formatMoney(priced.amount)} ${priced.currency} — published from ${formatDate(priced.effectiveFrom)}`
                : 'The schedule publishes no figure for this fee today.'
          }
        >
          <Select id="charge-code" fullWidth value={code} onChange={(event) => setCode(event.target.value as FeeCode)}>
            {feeCodes.map((option) => (
              <option key={option} value={option}>
                {feeCodeLabel(option)}
              </option>
            ))}
          </Select>
        </IntakeField>

        <IntakeField
          label="Reason"
          htmlFor="charge-reason"
          hint="Required. This is money the customer will be asked for."
        >
          <Input
            id="charge-reason"
            value={reason}
            onChange={(event) => setReason(event.target.value)}
            placeholder="Why this fee is being raised"
          />
        </IntakeField>
      </IntakeFields>

      <Button
        className="mt-4"
        disabled={serviceAccountId === '' || reason.trim() === '' || priced === undefined || raise.isPending}
        onClick={() => raise.mutate()}
      >
        {raise.isPending ? 'Raising…' : 'Raise fee'}
      </Button>

      {/* The id is on the form so a screen reader announces which customer is being charged. */}
      <span className="sr-only" data-customer-id={customerId} />
    </div>
  );
}

const chargeColumns: Column<AccountCharge>[] = [
  {
    key: 'code',
    header: 'Fee',
    wide: true,
    primary: true,
    sortValue: (row) => row.code,
    cell: (row) => (
      <span className="block min-w-0">
        <span className="text-heading block truncate font-medium">{feeCodeLabel(row.code)}</span>
        <span className="text-muted block truncate text-xs">{row.reason}</span>
      </span>
    ),
  },
  {
    key: 'accountNumber',
    header: 'Account',
    sortValue: (row) => row.accountNumber,
    cell: (row) => <span className="text-muted tabular text-xs font-medium">{row.accountNumber}</span>,
  },
  {
    key: 'amount',
    header: 'Amount',
    align: 'right',
    sortValue: (row) => row.amount,
    cell: (row) => <span className="tabular">{formatMoney(row.amount)}</span>,
  },
  {
    key: 'raisedOn',
    header: 'Raised',
    sortValue: (row) => row.raisedOn,
    cell: (row) => <span className="text-muted">{formatDate(row.raisedOn)}</span>,
  },
  {
    key: 'status',
    header: 'Status',
    sortValue: (row) => row.status,
    cell: (row) => <StatusPill status={row.status} tone={chargeStatusTone(row.status)} />,
  },
  {
    key: 'billNumber',
    header: 'Bill',
    align: 'right',
    sortValue: (row) => row.billNumber ?? '',
    cell: (row) => <span className="text-muted tabular text-xs">{row.billNumber ?? '—'}</span>,
  },
];
