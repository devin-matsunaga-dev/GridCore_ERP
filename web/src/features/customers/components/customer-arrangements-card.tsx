import { useMutation, useQueryClient } from '@tanstack/react-query';
import { HandCoins } from 'lucide-react';
import { useMemo, useState } from 'react';
import {
  arrangementApi,
  arrangementKeys,
  delinquencyKeys,
  useArrangementLimits,
  useDelinquency,
  usePaymentArrangements,
  type ArrangementInstalment,
  type CustomerClass,
  type PaymentArrangement,
  type ServiceAccount,
} from '@/api/customers';
import { toast } from '@/components/feedback/toast';
import { EmptyState } from '@/components/registry/empty-state';
import { ErrorState } from '@/components/registry/error-state';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import { StatusPill } from '@/components/ui/status';
import { formatDate, formatMoney } from '@/lib/format';
import { IntakeField, IntakeFields } from '../registration/components/intake-field';
import {
  arrangeableAccounts,
  arrangementLabel,
  arrangementTone,
  awaitsApproval,
  canPropose,
  isOverdue,
  limitFor,
  nextInstalment,
  previewSchedule,
  progressOf,
  sortArrangements,
  standingArrangement,
  willNeedApproval,
} from '../arrangements';

/**
 * What Customer Service does instead of disconnecting: one account's payment arrangements, and the
 * desk that makes one (WP-2.20).
 *
 * **It creates no money and never touches a bill.** The customer still owes exactly what the bills
 * say; this records how and when it will arrive. That is why there is no amount field a rep can
 * invent — the arrears comes from the delinquency picture the host computes, and the host refuses
 * anything above it.
 *
 * **The queries live in the card rather than at the page**, the call the charges and delinquency
 * tabs both made: an arrangement is per SERVICE ACCOUNT rather than per customer, so a 360 that
 * fetched one on every open would fetch one per supply, for a tab most telephone calls never reach.
 */
export function CustomerArrangementsCard({
  accounts,
  customerClass,
}: {
  accounts: readonly ServiceAccount[];
  customerClass: CustomerClass;
}) {
  const chooseable = useMemo(() => arrangeableAccounts(accounts), [accounts]);
  const [serviceAccountId, setServiceAccountId] = useState(chooseable[0]?.id ?? '');

  const selected = serviceAccountId === '' ? undefined : serviceAccountId;
  const arrangements = usePaymentArrangements(selected);

  // The arrears an arrangement may be made against, and the account's class — both the host's own
  // figures, read from the picture WP-2.19 already publishes rather than computed here.
  const picture = useDelinquency(selected);

  if (chooseable.length === 0) {
    return (
      <EmptyState
        icon={HandCoins}
        title="No service accounts"
        message="An arrangement is a promise about one supply's arrears at one premise. This customer holds none yet."
      />
    );
  }

  const rows = sortArrangements(arrangements.data ?? []);

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader>
          <CardTitle>Payment arrangements</CardTitle>
        </CardHeader>
        <CardContent className="space-y-5">
          <IntakeFields>
            <IntakeField
              label="Account"
              htmlFor="arrangement-account"
              hint="A customer may take several supplies, and each is arranged on its own."
            >
              <Select
                id="arrangement-account"
                fullWidth
                value={serviceAccountId}
                onChange={(event) => setServiceAccountId(event.target.value)}
              >
                {chooseable.map((account) => (
                  <option key={account.id} value={account.id}>
                    {account.accountNumber} · {account.serviceType} · {account.status}
                  </option>
                ))}
              </Select>
            </IntakeField>
          </IntakeFields>

          {arrangements.isPending && <Skeleton className="h-32 w-full" />}

          {arrangements.isError && (
            <ErrorState error={arrangements.error} onRetry={() => void arrangements.refetch()} />
          )}

          {arrangements.data && selected && (
            <ProposeForm
              serviceAccountId={selected}
              arrangements={rows}
              customerClass={customerClass}
              pastDueAmount={picture.data?.arrears.pastDueAmount ?? 0}
              accountNumber={picture.data?.accountNumber}
              isArrearsLoading={picture.isPending}
            />
          )}
        </CardContent>
      </Card>

      {arrangements.data &&
        (rows.length === 0 ? (
          <Card>
            <CardContent className="pt-6">
              <EmptyState
                icon={HandCoins}
                title="No arrangements"
                message="Promises to pay by instalment appear here. While one is in force the supply is not cut off, and a missed instalment is what ends that."
              />
            </CardContent>
          </Card>
        ) : (
          rows.map((arrangement) => (
            <ArrangementCard key={arrangement.id} arrangement={arrangement} />
          ))
        ))}
    </div>
  );
}

/** The desk that makes a promise: an amount inside the arrears, a down payment and a count. */
function ProposeForm({
  serviceAccountId,
  arrangements,
  customerClass,
  pastDueAmount,
  accountNumber,
  isArrearsLoading,
}: {
  serviceAccountId: string;
  arrangements: readonly PaymentArrangement[];
  customerClass: CustomerClass;
  pastDueAmount: number;
  accountNumber?: string;
  isArrearsLoading: boolean;
}) {
  const [balance, setBalance] = useState('');
  const [downPayment, setDownPayment] = useState('');
  const [instalmentCount, setInstalmentCount] = useState('3');

  const limits = useArrangementLimits();
  const queryClient = useQueryClient();

  const standing = standingArrangement(arrangements);
  const allowed = canPropose(arrangements);

  const balanceValue = Number(balance);
  const downValue = downPayment === '' ? 0 : Number(downPayment);
  const countValue = Number(instalmentCount);

  const preview = useMemo(
    () => previewSchedule(balanceValue, downValue, countValue),
    [balanceValue, downValue, countValue],
  );

  // The customer's OWN ceiling, read off the published list: a business owing four thousand dollars
  // over six months is ordinary and a household owing the same is not. The host judges again, on the
  // ceilings as they stand in the database — this only decides what the rep is told before they read
  // a schedule out to somebody.
  const limit = limitFor(limits.data ?? [], customerClass);
  const needsApproval = willNeedApproval(limit, balanceValue, countValue);

  const propose = useMutation({
    mutationFn: () =>
      arrangementApi.propose(serviceAccountId, {
        arrearsBalance: balanceValue,
        downPayment: downValue,
        instalmentCount: countValue,
      }),
    onSuccess: (arrangement) => {
      toast.success(
        `${arrangement.arrangementNumber} proposed on ${arrangement.accountNumber}.`,
        arrangement.requiresApproval
          ? 'It is beyond what a representative may agree alone, so it needs approving before it takes effect.'
          : 'It is not in force until it is activated.',
      );

      setBalance('');
      setDownPayment('');
      void queryClient.invalidateQueries({ queryKey: arrangementKeys.all });
      void queryClient.invalidateQueries({ queryKey: delinquencyKeys.all });
    },
    onError: (error) => toast.apiError(error, 'That arrangement could not be made.'),
  });

  if (!allowed) {
    return (
      <div className="border-border rounded-card border p-4">
        <p className="text-heading text-[13px] font-medium">One promise at a time</p>
        <p className="text-muted mt-1.5 text-[13px]">
          {standing?.arrangementNumber} is {arrangementLabel(standing?.standing ?? 'Proposed').toLowerCase()} on this
          account. Settle or review it before making another — two schedules would be two answers to
          what the customer has agreed to pay.
        </p>
      </div>
    );
  }

  return (
    <div className="border-border space-y-4 rounded-card border p-4">
      <div>
        <p className="text-heading text-[13px] font-medium">Arrange payment</p>
        <p className="text-muted mt-1.5 text-[13px]">
          {isArrearsLoading
            ? 'Reading what is past due…'
            : /*
                The ceiling stated before the field rather than after the refusal. An arrangement
                records how an EXISTING debt will be paid; it never creates one, so there is nothing
                above this figure to promise.
              */
              `${formatMoney(pastDueAmount)} is past due on ${accountNumber ?? 'this account'}. An arrangement may cover up to that and no more.`}
        </p>
      </div>

      <IntakeFields>
        <IntakeField label="Amount to arrange" htmlFor="arrangement-balance">
          <Input
            id="arrangement-balance"
            type="number"
            min="0"
            step="0.01"
            value={balance}
            onChange={(event) => setBalance(event.target.value)}
          />
        </IntakeField>

        <IntakeField
          label="Down payment"
          htmlFor="arrangement-down-payment"
          hint="Taken today. Leave empty where none is."
        >
          <Input
            id="arrangement-down-payment"
            type="number"
            min="0"
            step="0.01"
            value={downPayment}
            onChange={(event) => setDownPayment(event.target.value)}
          />
        </IntakeField>

        <IntakeField label="Instalments" htmlFor="arrangement-instalments">
          <Input
            id="arrangement-instalments"
            type="number"
            min="1"
            step="1"
            value={instalmentCount}
            onChange={(event) => setInstalmentCount(event.target.value)}
          />
        </IntakeField>
      </IntakeFields>

      {preview.length > 0 && (
        <div>
          <p className="text-muted text-[13px] font-medium">The customer would pay</p>
          <ul className="mt-2 flex flex-wrap gap-2">
            {preview.map((line) => (
              <li
                key={line.sequence}
                className="border-border text-body tabular rounded-card border px-3 py-1.5 text-[13px]"
              >
                {formatMoney(line.amount)}
                {line.isDownPayment && <span className="text-muted ml-1.5 text-xs">today</span>}
              </li>
            ))}
          </ul>
        </div>
      )}

      {needsApproval && limit && (
        <p className="text-body text-[13px]">
          {/*
            Said BEFORE the schedule is read out to a customer, not after the request is refused: a
            rep who has promised something they cannot deliver has to ring back.
          */}
          This is beyond the {formatMoney(limit.maximumBalance)} over {limit.maximumInstalments}{' '}
          instalments a representative may agree alone, so it will go to a supervisor for approval
          before it takes effect.
        </p>
      )}

      <Button
        disabled={propose.isPending || preview.length === 0}
        onClick={() => propose.mutate()}
      >
        {propose.isPending ? 'Recording…' : 'Propose arrangement'}
      </Button>
    </div>
  );
}

/** One promise: where it stands, what it protects, and the schedule behind it. */
function ArrangementCard({ arrangement }: { arrangement: PaymentArrangement }) {
  const queryClient = useQueryClient();
  const next = nextInstalment(arrangement);
  const progress = progressOf(arrangement);

  const activate = useMutation({
    mutationFn: () => arrangementApi.activate(arrangement.serviceAccountId, arrangement.id),
    onSuccess: (result) => {
      toast.success(
        `${result.arrangementNumber} is in force.`,
        'While it stands the supply is not disconnected for non-payment.',
      );

      void queryClient.invalidateQueries({ queryKey: arrangementKeys.all });
      void queryClient.invalidateQueries({ queryKey: delinquencyKeys.all });
    },
    onError: (error) => toast.apiError(error, 'That arrangement could not be brought into force.'),
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle>{arrangement.arrangementNumber}</CardTitle>
      </CardHeader>
      <CardContent className="space-y-5">
        <div className="flex flex-wrap items-center gap-3">
          <StatusPill
            status={arrangementLabel(arrangement.standing)}
            tone={arrangementTone(arrangement.standing)}
          />
          <span className="text-muted text-[13px]">
            {formatMoney(arrangement.arrearsBalance)} arranged on {formatDate(arrangement.arrangedOn)}
            {arrangement.activatedOn ? `, in force from ${formatDate(arrangement.activatedOn)}` : ''}
          </span>
        </div>

        {/*
          The one sentence a rep reads out. Whether the supply is protected is the HOST's answer —
          `suppressesDisconnection` — and never something this screen works out from the status.
        */}
        <p className="text-body text-[13px]">
          {arrangement.suppressesDisconnection
            ? 'This account is not disconnected for non-payment while the arrangement is kept.'
            : arrangement.standing === 'Broken'
              ? 'An instalment passed its due date unpaid, so the arrangement no longer protects this account. A broken arrangement is replaced, never resumed.'
              : arrangement.standing === 'Kept'
                ? 'Every instalment arrived. The promise is finished.'
                : 'It is not in force yet, so it protects nothing.'}
        </p>

        <div className="grid gap-4 sm:grid-cols-3">
          <Figure label="Paid" value={formatMoney(arrangement.paidAmount)} note={`${Math.round(progress * 100)}% of the schedule`} />
          <Figure
            label="Still promised"
            value={formatMoney(arrangement.outstandingAmount)}
            note={next ? `Next ${formatMoney(next.amount)} on ${formatDate(next.dueDate)}` : 'Nothing left to pay.'}
          />
          <Figure
            label="Down payment"
            value={formatMoney(arrangement.downPayment)}
            note={arrangement.downPayment > 0 ? 'Taken at the counter.' : 'None was taken.'}
          />
        </div>

        <Schedule arrangement={arrangement} />

        {arrangement.status === 'Proposed' && (
          <div className="border-border flex flex-wrap items-center justify-between gap-3 rounded-card border px-4 py-3">
            <span className="min-w-0">
              <span className="text-heading block text-[13px] font-medium">Not in force yet</span>
              <span className="text-muted block text-xs">
                {awaitsApproval(arrangement)
                  ? `Beyond the ${formatMoney(arrangement.limitMaximumBalance)} over ${arrangement.limitMaximumInstalments} a representative may agree alone — a supervisor has to approve it first.`
                  : 'Bringing it into force is what stops the supply being disconnected for non-payment.'}
              </span>
            </span>
            <Button size="sm" disabled={activate.isPending} onClick={() => activate.mutate()}>
              {activate.isPending ? 'Recording…' : 'Bring into force'}
            </Button>
          </div>
        )}
      </CardContent>
    </Card>
  );
}

/** The dated lines, with the overdue ones called out. */
function Schedule({ arrangement }: { arrangement: PaymentArrangement }) {
  // Today, for the overdue marking only. Whether the arrangement is broken because of it is the
  // host's answer, on `standing`.
  const today = new Date().toISOString().slice(0, 10);

  return (
    <div className="border-border overflow-x-auto rounded-card border">
      <table className="w-full min-w-[480px] text-[13px]">
        <thead>
          <tr className="text-muted border-border border-b text-left text-[13px] font-medium">
            <th scope="col" className="px-4 py-2.5">Due</th>
            <th scope="col" className="px-4 py-2.5 text-right">Amount</th>
            <th scope="col" className="px-4 py-2.5 text-right">Paid</th>
            <th scope="col" className="px-4 py-2.5">Status</th>
          </tr>
        </thead>
        <tbody>
          {arrangement.instalments.map((instalment) => (
            <Line key={instalment.id} instalment={instalment} today={today} />
          ))}
          <tr>
            <td className="text-heading px-4 py-2.5 font-semibold">Total</td>
            <td className="text-heading tabular px-4 py-2.5 text-right font-semibold">
              {formatMoney(arrangement.scheduledAmount)}
            </td>
            <td className="text-heading tabular px-4 py-2.5 text-right font-semibold">
              {formatMoney(arrangement.paidAmount)}
            </td>
            <td />
          </tr>
        </tbody>
      </table>
    </div>
  );
}

function Line({ instalment, today }: { instalment: ArrangementInstalment; today: string }) {
  const overdue = isOverdue(instalment, today);

  return (
    <tr className="border-border border-b last:border-0">
      <td className="text-body px-4 py-2.5">
        {formatDate(instalment.dueDate)}
        {instalment.isDownPayment && <span className="text-muted ml-2 text-xs">Down payment</span>}
      </td>
      <td className="text-heading tabular px-4 py-2.5 text-right">{formatMoney(instalment.amount)}</td>
      <td className="text-body tabular px-4 py-2.5 text-right">{formatMoney(instalment.paidAmount)}</td>
      <td className="px-4 py-2.5">
        <StatusPill
          status={instalment.isSettled ? 'Paid' : overdue ? 'Missed' : 'Due'}
          tone={instalment.isSettled ? 'success' : overdue ? 'danger' : 'neutral'}
        />
      </td>
    </tr>
  );
}

function Figure({ label, value, note }: { label: string; value: string; note: string }) {
  return (
    <div className="border-border rounded-card border p-4">
      <p className="text-muted text-[13px] font-medium">{label}</p>
      <p className="text-heading tabular mt-1.5 text-[30px] leading-none font-bold">{value}</p>
      <p className="text-muted mt-2 text-xs">{note}</p>
    </div>
  );
}
