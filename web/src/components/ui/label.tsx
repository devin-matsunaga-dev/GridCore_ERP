import type * as React from 'react';
import { cn } from '@/lib/utils';

export function Label({ className, ...props }: React.ComponentProps<'label'>) {
  return (
    <label
      data-slot="label"
      className={cn('text-heading text-[13px] leading-none font-medium', className)}
      {...props}
    />
  );
}
