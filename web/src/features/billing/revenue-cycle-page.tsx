import { RotateCcw } from 'lucide-react';
import { useCallback, useMemo, useState } from 'react';
import type { JournalEntry } from '@/api/finance';
import { PageHeader } from '@/components/registry/page-header';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import { formatCount } from '@/lib/format';
import { cn } from '@/lib/utils';
import { AccountingStep } from './components/accounting-step';
import { AccountStep } from './components/account-step';
import { BillStep } from './components/bill-step';
import { CustomerStep } from './components/customer-step';
import { MeterStep } from './components/meter-step';
import { PaymentStep } from './components/payment-step';
import { ReadingStep } from './components/reading-step';
import { StepCard } from './components/step-card';
import {
  completedStepCount,
  nextCycleCode,
  revenueCycleStepIds,
  revenueCycleSteps,
  stepStatus,
  type RevenueCycleState,
} from './revenue-cycle';

/**
 * The Revenue Cycle, walked on screen: SPEC.md's nine steps, top to bottom, each one performing a
 * real act against the real API and reporting the downstream effect it caused.
 *
 * This is the first of the two demonstration workflows and it is the reason the ledger has anything
 * in it. WP-2.6 shipped no Finance seeder, deliberately: `BillsDemoSeeder` writes bills straight to
 * Billing's tables and publishes nothing, so inventing matching journal entries would have put
 * figures in a trial balance that no upstream fact explained. A freshly seeded demo world therefore
 * opens on an empty ledger, and **this screen is what fills it** — by raising real events that
 * Finance's consumers post from.
 *
 * It is also GridCore's first screen that writes. The registry screens are read-only by design
 * (WP-1.5), so the react-hook-form + zod + shared-toast shape CONVENTIONS.md asks for starts here.
 */
export function RevenueCyclePage() {
  const [state, setState] = useState<RevenueCycleState>({});

  // Fixed when the walk starts rather than read from the clock as the reading step runs, so the
  // code on screen is the code that will be used — and so re-rendering cannot change it underfoot.
  const [cycleCode, setCycleCode] = useState(() => nextCycleCode());

  const patch = useCallback((next: Partial<RevenueCycleState>) => {
    setState((current) => ({ ...current, ...next }));
  }, []);

  const onPosted = useCallback(
    (postedEntries: JournalEntry[]) => {
      setState((current) =>
        // Guarded against re-reporting the same arrival: the accounting step polls, and an
        // unconditional set would re-render it into asking again.
        current.postedEntries?.length === postedEntries.length ? current : { ...current, postedEntries },
      );
    },
    [],
  );

  const completed = completedStepCount(state);

  const bodies = useMemo(
    () => ({
      customer: (
        <CustomerStep
          result={state.customer && state.location ? { customer: state.customer, location: state.location } : undefined}
          onDone={(result) => patch(result)}
        />
      ),
      'service-account':
        state.customer && state.location ? (
          <AccountStep
            customer={state.customer}
            location={state.location}
            result={state.account}
            onDone={(account) => patch({ account })}
          />
        ) : null,
      meter: state.location ? (
        <MeterStep location={state.location} result={state.meter} onDone={(meter) => patch({ meter })} />
      ) : null,
      reading: state.meter ? (
        <ReadingStep
          meter={state.meter}
          defaultCycleCode={cycleCode}
          result={state.cycle && state.reading ? { cycle: state.cycle, reading: state.reading } : undefined}
          onDone={(result) => {
            setCycleCode(result.cycle.cycleCode);
            patch(result);
          }}
        />
      ) : null,
      bill:
        state.account && state.reading ? (
          <BillStep
            account={state.account}
            cycleCode={state.cycle?.cycleCode ?? cycleCode}
            result={state.billingRun && state.bill ? { billingRun: state.billingRun, bill: state.bill } : undefined}
            onDone={(result) => patch(result)}
          />
        ) : null,
      payment: state.bill ? (
        <PaymentStep
          bill={state.bill}
          result={state.payment && state.settledBill ? { payment: state.payment, settledBill: state.settledBill } : undefined}
          onDone={(result) => patch(result)}
        />
      ) : null,
      accounting:
        state.account && state.payment && state.settledBill ? (
          <AccountingStep account={state.account} bill={state.settledBill} state={state} onPosted={onPosted} />
        ) : null,
    }),
    [state, cycleCode, patch, onPosted],
  );

  return (
    <div className="space-y-6">
      <PageHeader
        title="Revenue cycle"
        subtitle="Register a customer, meter them, read the meter, bill it, take the money, and watch the books post themselves."
        actions={
          <Button
            variant="secondary"
            onClick={() => {
              setState({});
              setCycleCode(nextCycleCode());
            }}
            disabled={completed === 0}
          >
            <RotateCcw aria-hidden="true" />
            Start again
          </Button>
        }
      />

      <Card className="px-6 py-4">
        <div className="flex flex-wrap items-center justify-between gap-x-6 gap-y-3">
          <ol className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-2">
            {revenueCycleSteps.map((step, index) => {
              const status = stepStatus(state, step.id);

              return (
                <li key={step.id} className="flex items-center gap-2">
                  <span
                    className={cn(
                      'flex size-6 items-center justify-center rounded-full text-xs font-semibold tabular-nums',
                      status === 'done' && 'bg-success text-white',
                      status === 'active' && 'bg-primary text-primary-foreground',
                      status === 'waiting' && 'bg-neutral-soft text-neutral',
                    )}
                    aria-hidden="true"
                  >
                    {index + 1}
                  </span>
                  <span
                    className={cn(
                      'text-[13px]',
                      status === 'waiting' ? 'text-muted' : 'text-heading font-medium',
                    )}
                  >
                    {step.title}
                  </span>
                  {index < revenueCycleSteps.length - 1 && (
                    <span className="text-muted mx-1 hidden text-xs lg:inline" aria-hidden="true">
                      ›
                    </span>
                  )}
                </li>
              );
            })}
          </ol>

          <p className="text-muted tabular shrink-0 text-[13px]">
            {formatCount(completed)} of {formatCount(revenueCycleStepIds.length)} done
          </p>
        </div>
      </Card>

      <div className="space-y-4">
        {revenueCycleSteps.map((step, index) => (
          <StepCard key={step.id} step={step} index={index + 1} status={stepStatus(state, step.id)}>
            {bodies[step.id]}
          </StepCard>
        ))}
      </div>
    </div>
  );
}
