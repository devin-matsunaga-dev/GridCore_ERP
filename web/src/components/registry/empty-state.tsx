import type { LucideIcon } from 'lucide-react';
import type * as React from 'react';
import { cn } from '@/lib/utils';

/**
 * DESIGN.md: "friendly empty states with icon+action", never a blank panel. Sized for the inside
 * of a card, so a table that filtered down to nothing keeps its header and its filters on screen.
 */
export function EmptyState({
  icon: Icon,
  title,
  message,
  action,
  className,
}: {
  icon: LucideIcon;
  title: string;
  message?: React.ReactNode;
  action?: React.ReactNode;
  className?: string;
}) {
  return (
    <div className={cn('flex flex-col items-center justify-center px-6 py-14 text-center', className)}>
      <span className="bg-primary-soft flex size-12 items-center justify-center rounded-full">
        <Icon className="text-primary size-6" strokeWidth={1.5} aria-hidden="true" />
      </span>
      <h3 className="text-heading mt-4 text-[15px] font-semibold">{title}</h3>
      {message && <p className="text-body mt-1.5 max-w-sm text-[13px]">{message}</p>}
      {action && <div className="mt-5">{action}</div>}
    </div>
  );
}
