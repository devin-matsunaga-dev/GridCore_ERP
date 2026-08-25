import type { DepositRule } from '@/api/customers';
import { cn } from '@/lib/utils';
import { reviewFacts, type IntakeValues } from '../registration';

/**
 * Step 5 — everything above, before anything is written.
 *
 * Nothing has been sent yet. The customer, the premise, the account and the deposit all reach the
 * host as one request and commit in one transaction, so a wizard abandoned on any earlier step —
 * or on this one — leaves no half-registered customer behind. That claim is the reason this is one
 * call rather than four, and the line under the list says so on screen.
 */
export function ReviewStep({
  values,
  assessment,
  premiseLabel,
}: {
  values: IntakeValues;
  assessment: DepositRule | undefined;
  premiseLabel: string | undefined;
}) {
  const facts = reviewFacts(values, assessment, premiseLabel);

  return (
    <div className="space-y-4">
      <dl className="grid gap-x-6 gap-y-3 sm:grid-cols-2 lg:grid-cols-3">
        {facts.map((fact) => (
          <div key={fact.label} className="min-w-0">
            <dt className="text-muted text-[13px] font-medium">{fact.label}</dt>
            <dd className={cn('text-heading mt-0.5 truncate text-sm', fact.numeric && 'tabular')}>
              {fact.value}
            </dd>
          </div>
        ))}
      </dl>

      <p className="text-muted text-xs">
        Registering writes the customer, the premise and the service account in one transaction.
        Nothing above has been saved yet.
      </p>
    </div>
  );
}
