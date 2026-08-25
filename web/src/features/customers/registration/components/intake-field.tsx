import type { ReactNode } from 'react';
import { Label } from '@/components/ui/label';
import { cn } from '@/lib/utils';

/**
 * A labelled input with its error underneath, and the grid the wizard's inputs sit in.
 *
 * The same shape `features/billing/components/step-card.tsx` gives the demonstration walk's fields
 * — DESIGN.md's forms section is one rule, so two wizards look alike by having the same parts.
 * Deliberately a second small copy rather than a shared component: promoting them belongs to
 * whichever work package needs a third wizard, and would touch seven files of WP-2.7's that this
 * one has no business editing.
 */

export function IntakeField({
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
  hint?: ReactNode;
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
export function IntakeFields({ children, className }: { children: ReactNode; className?: string }) {
  return <div className={cn('grid gap-4 sm:grid-cols-2 lg:grid-cols-3', className)}>{children}</div>;
}
