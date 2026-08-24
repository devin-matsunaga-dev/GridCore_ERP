import { ArrowDown, ArrowUp, type LucideIcon } from 'lucide-react';
import { Card } from '@/components/ui/card';
import { formatPercent } from '@/lib/format';
import { cn } from '@/lib/utils';
import { Sparkline } from './sparkline';

export type KpiCardProps = {
  label: string;
  value: string;
  icon: LucideIcon;
  /** Signed fraction: -0.064 renders as a 6.4% fall. */
  change: number;
  comparedTo: string;
  /** Cost falling is good; assets falling is not. Drives the delta colour, per DESIGN.md. */
  fallIsGood?: boolean;
  trend?: readonly number[];
};

/** Tinted icon circle + label, big value, then the delta line with its trend to the right. */
export function KpiCard({
  label,
  value,
  icon: Icon,
  change,
  comparedTo,
  fallIsGood = false,
  trend,
}: KpiCardProps) {
  const isFall = change < 0;
  const isGoodNews = isFall === fallIsGood;
  const DeltaIcon = isFall ? ArrowDown : ArrowUp;
  const sentiment = isGoodNews ? 'text-success' : 'text-danger';

  return (
    <Card className="px-5 py-4">
      <div className="flex items-center gap-3">
        <span className="bg-primary-soft flex size-9 shrink-0 items-center justify-center rounded-full">
          <Icon className="text-primary size-[18px]" strokeWidth={1.75} aria-hidden="true" />
        </span>
        <p className="text-body min-w-0 text-[13px] leading-snug font-medium">{label}</p>
      </div>

      <p className="text-heading tabular mt-2.5 text-[28px] leading-none font-bold">{value}</p>

      <div className="mt-2.5 flex items-end justify-between gap-3">
        <p className="flex items-center gap-1.5 text-[13px]">
          <DeltaIcon className={cn('size-4', sentiment)} strokeWidth={2} aria-hidden="true" />
          <span className={cn('tabular font-medium', sentiment)}>{formatPercent(Math.abs(change))}</span>
          <span className="text-muted">vs {comparedTo}</span>
        </p>

        {trend && (
          <Sparkline values={trend} tone="var(--success)" className="hidden h-6 w-20 shrink-0 2xl:block" />
        )}
      </div>
    </Card>
  );
}
