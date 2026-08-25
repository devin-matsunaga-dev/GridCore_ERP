import { Check } from 'lucide-react';
import type { ReactNode } from 'react';
import { Card } from '@/components/ui/card';
import { Label } from '@/components/ui/label';
import { formatMoney } from '@/lib/format';
import { cn } from '@/lib/utils';
import type { RevenueCycleStep, RevenueCycleStepStatus } from '../revenue-cycle';

/**
 * The shared parts of a wizard step: the numbered card it sits in, the label/value list a finished
 * step reports itself with, and the field wrapper its inputs use.
 *
 * Grouped in one file the way `filter-bar.tsx` groups the registry filters — these are five pieces
 * of one thing, and a step component wants all of them.
 */

export function StepCard({
  step,
  index,
  status,
  children,
}: {
  step: RevenueCycleStep;
  /** Position in the walk, from 1 — what the rail and the card badge both count by. */
  index: number;
  status: RevenueCycleStepStatus;
  children?: ReactNode;
}) {
  const done = status === 'done';
  const waiting = status === 'waiting';

  return (
    <Card
      // A waiting step is dimmed rather than hidden: the whole point of a wizard for a demonstration
      // is that somebody can see where the walk is going before it gets there.
      className={cn('transition-opacity', waiting && 'opacity-60')}
      aria-current={status === 'active' ? 'step' : undefined}
      data-status={status}
      data-step={step.id}
    >
      <div className="flex gap-4 px-6 pt-5 pb-5">
        <span
          className={cn(
            'mt-0.5 flex size-8 shrink-0 items-center justify-center rounded-full text-[13px] font-semibold tabular-nums',
            done && 'bg-success-soft text-success',
            status === 'active' && 'bg-primary text-primary-foreground',
            waiting && 'bg-neutral-soft text-neutral',
          )}
        >
          {done ? <Check className="size-4" strokeWidth={2.5} aria-hidden="true" /> : index}
          <span className="sr-only">
            {done ? 'Completed step' : status === 'active' ? 'Current step' : 'Upcoming step'} {index}
          </span>
        </span>

        <div className="min-w-0 flex-1">
          <h3 className="text-heading text-base leading-tight font-semibold">{step.title}</h3>
          <p className="text-muted mt-1 text-[13px]">{step.summary}</p>
          <p className="text-muted mt-1 text-xs">{step.specSteps.join(' · ')}</p>

          {children && <div className="mt-4">{children}</div>}
        </div>
      </div>
    </Card>
  );
}

/** One fact a finished step reports. `null` renders as an em dash rather than an empty cell. */
export type StepFact = {
  label: string;
  value: ReactNode;
  /** Right-aligns and tabular-forms the value — for money and counts. */
  numeric?: boolean;
};

/** What a step produced, as a compact label-over-value grid. */
export function StepFacts({ facts, className }: { facts: StepFact[]; className?: string }) {
  return (
    <dl className={cn('grid gap-x-6 gap-y-3 sm:grid-cols-2 lg:grid-cols-3', className)}>
      {facts.map((fact) => (
        <div key={fact.label} className="min-w-0">
          <dt className="text-muted text-[13px] font-medium">{fact.label}</dt>
          <dd className={cn('text-heading mt-0.5 truncate text-sm', fact.numeric && 'tabular')}>
            {fact.value ?? '—'}
          </dd>
        </div>
      ))}
    </dl>
  );
}

/** A labelled input, with the field error underneath it in the shape DESIGN.md asks for. */
export function StepField({
  label,
  htmlFor,
  error,
  hint,
  className,
  children,
}: {
  label: string;
  htmlFor: string;
  error?: string;
  hint?: string;
  className?: string;
  children: ReactNode;
}) {
  const describedBy = error ? `${htmlFor}-error` : hint ? `${htmlFor}-hint` : undefined;

  return (
    <div className={cn('min-w-0', className)}>
      <Label htmlFor={htmlFor}>{label}</Label>
      <div className="mt-1.5" aria-describedby={describedBy}>
        {children}
      </div>
      {error ? (
        <p id={`${htmlFor}-error`} className="text-danger mt-1 text-xs">
          {error}
        </p>
      ) : (
        hint && (
          <p id={`${htmlFor}-hint`} className="text-muted mt-1 text-xs">
            {hint}
          </p>
        )
      )}
    </div>
  );
}

/** The grid a step's inputs sit in. */
export function StepFields({ children, className }: { children: ReactNode; className?: string }) {
  return <div className={cn('grid gap-4 sm:grid-cols-2 lg:grid-cols-3', className)}>{children}</div>;
}

/** A money value in a fact list. Central formatting, per DESIGN.md's quality floor. */
export function Money({ value }: { value: number }) {
  return <span className="tabular">{formatMoney(value)}</span>;
}
