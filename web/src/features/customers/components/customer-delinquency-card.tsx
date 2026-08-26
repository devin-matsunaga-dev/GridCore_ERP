import { useMutation, useQueryClient } from '@tanstack/react-query';
import { ShieldAlert } from 'lucide-react';
import { useMemo, useState } from 'react';
import { billKeys } from '@/api/billing';
import {
  delinquencyApi,
  delinquencyKeys,
  useDelinquency,
  type Delinquency,
  type DisconnectionEligibility,
  type DunningNotice,
  type ServiceAccount,
} from '@/api/customers';
import { toast } from '@/components/feedback/toast';
import { ErrorState } from '@/components/registry/error-state';
import { EmptyState } from '@/components/registry/empty-state';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Select } from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import { StatusPill } from '@/components/ui/status';
import { formatDate, formatMoney } from '@/lib/format';
import { IntakeField, IntakeFields } from '../registration/components/intake-field';
import {
  delinquencyAccounts,
  eligibilitySummary,
  eligibilityTone,
  hasOffsetToApply,
  nextNoticeToServe,
  noticeLabel,
  noticeTone,
  occupiedBuckets,
  sortNotices,
} from '../delinquency';

/**
 * One account's delinquency: what is past due and how old it is, which notices have been served, and
 * where the account stands against the four disconnection tests (WP-2.19).
 *
 * **Reading is a read; evaluating moves money.** The picture below shows what the deposit offset
 * *would* come to — it is computed by the host and applied by nobody. Pressing "Apply deposit and
 * evaluate" is what actually sets the deposit against the arrears under CNMI Public Law 16-17, which
 * is why it is a button with a consequence spelled out beside it rather than something the page does
 * on open.
 *
 * **The queries live in the card rather than at the page**, the call the charges tab already made:
 * delinquency is per *account*, not per customer, and a 360 that fetched an arrears picture for every
 * supply on every open would pay for a tab most telephone calls never reach.
 */
export function CustomerDelinquencyCard({ accounts }: { accounts: readonly ServiceAccount[] }) {
  const chooseable = useMemo(() => delinquencyAccounts(accounts), [accounts]);
  const [serviceAccountId, setServiceAccountId] = useState(chooseable[0]?.id ?? '');

  const picture = useDelinquency(serviceAccountId === '' ? undefined : serviceAccountId);

  if (chooseable.length === 0) {
    return (
      <EmptyState
        icon={ShieldAlert}
        title="No service accounts"
        message="Arrears, dunning notices and disconnection eligibility are all about one supply at one premise. This customer holds none yet."
      />
    );
  }

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader>
          <CardTitle>Delinquency</CardTitle>
        </CardHeader>
        <CardContent className="space-y-5">
          <IntakeFields>
            <IntakeField
              label="Account"
              htmlFor="delinquency-account"
              hint="A customer may take several supplies, and each is delinquent on its own."
            >
              <Select
                id="delinquency-account"
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

          {picture.isPending && <Skeleton className="h-32 w-full" />}

          {picture.isError && (
            <ErrorState error={picture.error} onRetry={() => void picture.refetch()} />
          )}

          {picture.data && <ArrearsSummary picture={picture.data} />}
        </CardContent>
      </Card>

      {picture.data && <EligibilityCard picture={picture.data} />}
      {picture.data && <NoticesCard picture={picture.data} />}
    </div>
  );
}

/** What is owed, split into what is late and what is not, with the ageing beneath it. */
function ArrearsSummary({ picture }: { picture: Delinquency }) {
  const { arrears } = picture;
  const bands = occupiedBuckets(arrears);

  return (
    <div className="space-y-5">
      <div className="grid gap-4 sm:grid-cols-3">
        <Figure
          label="Past due"
          value={formatMoney(arrears.pastDueAmount)}
          note={
            arrears.isInArrears
              ? `Oldest ${arrears.daysPastDue} days late${arrears.oldestDueDate ? `, due ${formatDate(arrears.oldestDueDate)}` : ''}`
              : 'Nothing is late on this account.'
          }
        />
        {/*
          Stated beside the arrears rather than instead of it. A bill issued last week and due next
          month is money the utility is owed and is NOT money the customer is late with — the
          distinction the 1% late charge and the disconnection threshold both turn on.
        */}
        <Figure label="Not yet due" value={formatMoney(arrears.currentAmount)} note="Issued, owed, and not late." />
        <Figure
          label="Deposit held"
          value={formatMoney(picture.depositHeld)}
          note="Set against qualifying past-due amounts before any disconnection."
        />
      </div>

      {bands.length > 0 && (
        <div className="border-border overflow-x-auto rounded-card border">
          <table className="w-full min-w-[420px] text-[13px]">
            <thead>
              <tr className="text-muted border-border border-b text-left text-[13px] font-medium">
                <th scope="col" className="px-4 py-2.5">Age</th>
                <th scope="col" className="px-4 py-2.5 text-right">Amount</th>
              </tr>
            </thead>
            <tbody>
              {bands.map((bucket) => (
                <tr key={bucket.label} className="border-border border-b last:border-0">
                  <td className="text-body px-4 py-2.5">{bucket.label}</td>
                  <td className="text-heading tabular px-4 py-2.5 text-right">{formatMoney(bucket.amount)}</td>
                </tr>
              ))}
              <tr>
                <td className="text-heading px-4 py-2.5 font-semibold">Total outstanding</td>
                <td className="text-heading tabular px-4 py-2.5 text-right font-semibold">
                  {formatMoney(arrears.outstandingAmount)}
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

/** The four tests, what the deposit offset would come to, and the button that makes it so. */
function EligibilityCard({ picture }: { picture: Delinquency }) {
  const { eligibility } = picture;
  const queryClient = useQueryClient();

  const evaluate = useMutation({
    mutationFn: () => delinquencyApi.evaluate(picture.serviceAccountId),
    onSuccess: (result) => {
      toast.success(
        result.offsetAmount > 0
          ? `${formatMoney(result.offsetAmount)} of deposit applied to past-due bills.`
          : 'Evaluated. There was no deposit to apply.',
        result.eligibility.isEligible
          ? 'The account is eligible for disconnection for non-payment.'
          : 'The account is not eligible for disconnection.',
      );

      void queryClient.invalidateQueries({ queryKey: delinquencyKeys.all });
      void queryClient.invalidateQueries({ queryKey: ['customers'] });
      void queryClient.invalidateQueries({ queryKey: billKeys.all });
    },
    onError: (error) => toast.apiError(error, 'That account could not be evaluated.'),
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle>Disconnection eligibility</CardTitle>
      </CardHeader>
      <CardContent className="space-y-5">
        <div className="flex flex-wrap items-center gap-3">
          <StatusPill
            status={eligibility.isEligible ? 'Eligible' : 'Not eligible'}
            tone={eligibilityTone(eligibility)}
          />
          <p className="text-body text-[13px]">{eligibilitySummary(eligibility)}</p>
        </div>

        <ul className="space-y-2">
          {eligibility.tests.map((test) => (
            <li key={test.name} className="border-border flex flex-wrap items-baseline gap-x-3 gap-y-1 rounded-card border px-4 py-3">
              <StatusPill status={test.isSatisfied ? 'Met' : 'Outstanding'} tone={test.isSatisfied ? 'success' : 'warning'} />
              <span className="text-heading text-[13px] font-medium">{test.name}</span>
              <span className="text-muted w-full text-xs sm:w-auto">{test.detail}</span>
            </li>
          ))}
        </ul>

        <div className="border-border rounded-card border p-4">
          <p className="text-heading text-[13px] font-medium">Statutory deposit offset</p>
          <p className="text-muted mt-1.5 text-[13px]">
            {/*
              The consequence, spelled out before the button rather than in a toast after it. This is
              the one screen in GridCore where pressing a button spends the customer's own money, and
              it does so because the law requires it.
            */}
            CNMI Public Law 16-17 obliges the utility to set the deposit against qualifying past-due
            amounts before service is disconnected. Evaluating applies{' '}
            <span className="tabular font-medium">{formatMoney(eligibility.offsetAmount)}</span> to the
            oldest past-due bills first and records why on the ledger.
          </p>

          <Button
            className="mt-4"
            variant={hasOffsetToApply(eligibility) ? 'primary' : 'secondary'}
            disabled={evaluate.isPending}
            onClick={() => evaluate.mutate()}
          >
            {evaluate.isPending
              ? 'Evaluating…'
              : hasOffsetToApply(eligibility)
                ? 'Apply deposit and evaluate'
                : 'Evaluate for disconnection'}
          </Button>
        </div>
      </CardContent>
    </Card>
  );
}

/** What has been served, and the next notice the sequence asks for. */
function NoticesCard({ picture }: { picture: Delinquency }) {
  const notices = useMemo(() => sortNotices(picture.notices), [picture.notices]);
  const next = nextNoticeToServe(picture);
  const queryClient = useQueryClient();

  const serve = useMutation({
    mutationFn: () => delinquencyApi.serve(picture.serviceAccountId, { noticeType: next!.noticeType }),
    onSuccess: (notice) => {
      toast.success(
        `${noticeLabel(notice.noticeType)} served on ${notice.accountNumber}.`,
        notice.effectiveFrom
          ? `Disconnection may not be taken before ${formatDate(notice.effectiveFrom)}.`
          : undefined,
      );

      void queryClient.invalidateQueries({ queryKey: delinquencyKeys.all });
    },
    onError: (error) => toast.apiError(error, 'That notice could not be served.'),
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle>Dunning notices</CardTitle>
      </CardHeader>
      <CardContent className="space-y-4">
        {next && (
          <div className="border-border flex flex-wrap items-center justify-between gap-3 rounded-card border px-4 py-3">
            <span className="min-w-0">
              <span className="text-heading block truncate text-[13px] font-medium">
                {next.name} is due
              </span>
              <span className="text-muted block text-xs">
                Published at {formatMoney(next.minimumArrears)} and {next.daysPastDue} days past due
                {next.waitingPeriodDays > 0 ? `, with ${next.waitingPeriodDays} days to wait after service` : ''}.
              </span>
            </span>
            <Button size="sm" disabled={serve.isPending} onClick={() => serve.mutate()}>
              {serve.isPending ? 'Recording…' : 'Record as served'}
            </Button>
          </div>
        )}

        {notices.length === 0 ? (
          <EmptyState
            icon={ShieldAlert}
            title="No notices served"
            message="Reminders, delinquency notices and disconnection notices appear here as they go out. The record of one is what makes a disconnection defensible."
          />
        ) : (
          <ul className="space-y-2">
            {notices.map((notice) => (
              <NoticeRow key={notice.id} notice={notice} />
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}

function NoticeRow({ notice }: { notice: DunningNotice }) {
  return (
    <li className="border-border flex flex-wrap items-center justify-between gap-3 rounded-card border px-4 py-3">
      <span className="min-w-0">
        <span className="flex flex-wrap items-center gap-2">
          <StatusPill status={noticeLabel(notice.noticeType)} tone={noticeTone(notice.noticeType)} />
          <span className="text-muted text-xs">Served {formatDate(notice.servedOn)}</span>
        </span>
        <span className="text-muted mt-1 block truncate text-xs">
          {formatMoney(notice.arrearsAmount)} past due, {notice.daysPastDue} days late
          {notice.effectiveFrom ? ` · effective from ${formatDate(notice.effectiveFrom)}` : ''}
          {notice.actorName ? ` · ${notice.actorName}` : ''}
        </span>
      </span>
    </li>
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

/** Re-exported for the eligibility strip's tests, which are about the sentence rather than the DOM. */
export type { DisconnectionEligibility };
