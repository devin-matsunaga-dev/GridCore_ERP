import type * as React from 'react';
import { cn } from '@/lib/utils';

export function Input({ className, type, ...props }: React.ComponentProps<'input'>) {
  return (
    <input
      type={type}
      data-slot="input"
      className={cn(
        'border-border bg-card text-heading placeholder:text-muted rounded-field h-10 w-full border px-3 py-2 text-sm',
        'focus-visible:border-primary focus-visible:ring-ring/30 transition-[color,box-shadow] focus-visible:ring-[3px] focus-visible:outline-none',
        'aria-invalid:border-danger disabled:cursor-not-allowed disabled:opacity-50',
        className,
      )}
      {...props}
    />
  );
}
