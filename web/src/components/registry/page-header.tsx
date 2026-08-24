import type * as React from 'react';
import { cn } from '@/lib/utils';

/**
 * A registry page's title block. DESIGN.md type scale: 26/700 title, muted subline — the same
 * shape the dashboard greeting uses, so a registry reads as the same product.
 */
export function PageHeader({
  title,
  subtitle,
  actions,
  className,
}: {
  title: string;
  subtitle?: React.ReactNode;
  actions?: React.ReactNode;
  className?: string;
}) {
  return (
    <div className={cn('flex flex-wrap items-start justify-between gap-x-4 gap-y-3', className)}>
      <div className="min-w-0">
        <h2 className="text-heading text-[26px] leading-tight font-bold">{title}</h2>
        {subtitle && <p className="text-muted mt-0.5 text-sm">{subtitle}</p>}
      </div>
      {actions && <div className="flex shrink-0 flex-wrap items-center gap-2">{actions}</div>}
    </div>
  );
}
