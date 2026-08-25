import type { LucideIcon } from 'lucide-react';
import type * as React from 'react';
import { Card } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { cn } from '@/lib/utils';

/**
 * One figure at the top of the 360° page: tinted icon circle, label, big value, a line of context
 * beneath — DESIGN.md's KPI card, minus its delta.
 *
 * The delta is missing on purpose rather than by omission. The dashboard's card carries "▲ 6.4% vs
 * last month" because the dashboard has periods to compare; a customer's balance has no previous
 * period this page could honestly compute, and a fabricated arrow on a rep's screen is worse than
 * no arrow. The subline carries a fact instead — when the last payment landed, how many bills are
 * open — which is what a rep would have read the delta for anyway.
 */
export function SummaryTile({
  label,
  value,
  icon: Icon,
  detail,
  tone,
  isPending = false,
}: {
  label: string;
  value: string;
  icon: LucideIcon;
  detail?: React.ReactNode;
  /** Colours the value. Left alone, it is the ordinary heading colour. */
  tone?: 'danger' | 'success';
  isPending?: boolean;
}) {
  return (
    <Card className="px-5 py-4">
      <div className="flex items-center gap-3">
        <span className="bg-primary-soft flex size-9 shrink-0 items-center justify-center rounded-full">
          <Icon className="text-primary size-[18px]" strokeWidth={1.75} aria-hidden="true" />
        </span>
        <p className="text-body min-w-0 text-[13px] leading-snug font-medium">{label}</p>
      </div>

      {isPending ? (
        <Skeleton className="mt-3 h-7 w-28" />
      ) : (
        <p
          className={cn(
            'tabular mt-2.5 text-[28px] leading-none font-bold',
            tone === 'danger' ? 'text-danger' : tone === 'success' ? 'text-success' : 'text-heading',
          )}
        >
          {value}
        </p>
      )}

      <p className="text-muted mt-2.5 min-h-[18px] text-[13px]">
        {isPending ? <Skeleton className="h-3.5 w-24" /> : detail}
      </p>
    </Card>
  );
}
