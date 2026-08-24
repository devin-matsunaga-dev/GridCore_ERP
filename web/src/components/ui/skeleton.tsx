import type * as React from 'react';
import { cn } from '@/lib/utils';

/** DESIGN.md: skeleton shimmer, never spinners in cards. */
export function Skeleton({ className, ...props }: React.ComponentProps<'div'>) {
  return (
    <div
      data-slot="skeleton"
      className={cn('bg-neutral-soft animate-pulse rounded-md', className)}
      aria-hidden="true"
      {...props}
    />
  );
}
