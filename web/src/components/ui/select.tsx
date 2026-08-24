import { ChevronDown } from 'lucide-react';
import type * as React from 'react';
import { cn } from '@/lib/utils';

export type SelectProps = React.ComponentProps<'select'> & {
  /** Rendered as `<option>`s. Pass `children` instead for grouped or custom options. */
  options?: readonly string[];
};

/**
 * A native `<select>` under the card-header styling from DESIGN.md. Native on purpose: keyboard
 * behaviour, mobile pickers and screen-reader support come free, and it needs no extra dependency.
 */
export function Select({ className, options, children, ...props }: SelectProps) {
  return (
    <div className="relative inline-flex">
      <select
        data-slot="select"
        className={cn(
          'border-border bg-card text-heading rounded-control h-9 cursor-pointer appearance-none border py-1.5 pr-8 pl-3 text-[13px] font-medium',
          'hover:bg-canvas focus-visible:border-primary focus-visible:ring-ring/25 transition-colors focus-visible:ring-[3px] focus-visible:outline-none',
          className,
        )}
        {...props}
      >
        {children ?? options?.map((option) => <option key={option}>{option}</option>)}
      </select>
      <ChevronDown
        className="text-muted pointer-events-none absolute top-1/2 right-2.5 size-4 -translate-y-1/2"
        strokeWidth={1.75}
        aria-hidden="true"
      />
    </div>
  );
}
