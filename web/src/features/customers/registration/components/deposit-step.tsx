import { ShieldAlert } from 'lucide-react';
import type { UseFormReturn } from 'react-hook-form';
import type { DepositRule } from '@/api/customers';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import { formatMoney } from '@/lib/format';
import type { IntakeValues } from '../registration';
import { IntakeField, IntakeFields } from './intake-field';

/**
 * Step 4 — the deposit. THE SENSITIVE ONE.
 *
 * The figure is not typed and not guessed: it is read from the published schedule in the host's own
 * `customers.deposit_rules`, which is reference data shipped by migration. Changing what a class is
 * asked for is a migration, not a screen — so there is nothing on this step that can alter it.
 *
 * Collecting one is gated on `customers.deposit`, which is a narrower grant than the
 * `customers.write` that opened this wizard. A clerk who does not hold it can still complete the
 * intake — the box is disabled, the amount stays zero, and the reason is on screen rather than
 * arriving as a 403 on the last step of a five-step form.
 *
 * What this step does NOT do: hold, apply or refund. WP-2.12 owns that lifecycle and the ledger
 * entries behind it; this records what was taken and audits the act.
 */
export function DepositStep({
  form,
  assessment,
  isLoading,
  mayCollect,
}: {
  form: UseFormReturn<IntakeValues>;
  assessment: DepositRule | undefined;
  isLoading: boolean;
  mayCollect: boolean;
}) {
  const { errors } = form.formState;

  if (isLoading) return <Skeleton className="h-24" />;

  return (
    <div className="space-y-5">
      <div className="border-border bg-canvas rounded-card border px-4 py-3">
        <p className="text-muted text-[13px] font-medium">
          Assessed for a {form.watch('class').toLowerCase()} connection
        </p>
        <p className="text-heading tabular mt-0.5 text-[22px] leading-tight font-bold">
          {assessment ? formatMoney(assessment.amount) : '—'}
        </p>
        <p className="text-muted mt-1 text-xs">
          {assessment?.description ?? 'The deposit schedule could not be read.'}
        </p>
      </div>

      {!mayCollect && (
        <p className="text-warning bg-warning-soft flex items-start gap-2 rounded-card px-4 py-3 text-[13px]">
          <ShieldAlert className="mt-px size-4 shrink-0" aria-hidden="true" />
          <span>
            You do not hold <code className="tabular">customers.deposit</code>, so no deposit can be
            taken on this intake. Register the customer without one — somebody who holds the
            permission can collect it afterwards.
          </span>
        </p>
      )}

      <IntakeFields>
        <IntakeField
          label="Collected now"
          htmlFor="intake-deposit"
          error={errors.depositCollected?.message}
          hint={
            mayCollect
              ? 'Leave empty to waive it. Part of the assessed amount may be taken; more may not.'
              : 'Disabled: taking a deposit needs a permission you do not hold.'
          }
        >
          <Input
            id="intake-deposit"
            inputMode="decimal"
            placeholder="0.00"
            className="tabular"
            disabled={!mayCollect}
            aria-invalid={Boolean(errors.depositCollected)}
            {...form.register('depositCollected')}
          />
        </IntakeField>
      </IntakeFields>
    </div>
  );
}
