import type * as React from 'react';
import { cn } from '@/lib/utils';

export function Separator({
  className,
  orientation = 'horizontal',
  ...props
}: React.ComponentProps<'div'> & { orientation?: 'horizontal' | 'vertical' }) {
  return (
    <div
      data-slot="separator"
      role="separator"
      aria-orientation={orientation}
      className={cn('bg-border shrink-0', orientation === 'horizontal' ? 'h-px w-full' : 'h-6 w-px', className)}
      {...props}
    />
  );
}
