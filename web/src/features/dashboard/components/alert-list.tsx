import { CircleCheck, TriangleAlert } from 'lucide-react';
import { statusToneClasses } from '@/components/ui/status';
import { formatRelativeTime } from '@/lib/format';
import { cn } from '@/lib/utils';
import type { Alert } from '../demo-data';

/** Icon in a soft severity circle + title + subtext + relative time. */
export function AlertList({ alerts, now }: { alerts: Alert[]; now?: Date }) {
  // Defaulted here rather than in the parameter list: the reference instant for relative times is
  // meant to be re-read on every render, and a `new` expression in a default prop hides that.
  const reference = now ?? new Date();

  return (
    <ul className="divide-border divide-y">
      {alerts.map((alert) => {
        const tone = statusToneClasses(alert.tone);
        const Icon = alert.tone === 'success' ? CircleCheck : TriangleAlert;
        const occurredAt = new Date(reference.getTime() - alert.minutesAgo * 60_000);

        return (
          <li key={alert.id} className="flex items-start gap-3 py-3.5 first:pt-0 last:pb-0">
            <span className={cn('flex size-9 shrink-0 items-center justify-center rounded-full', tone.soft)}>
              <Icon className={cn('size-[18px]', tone.text)} strokeWidth={1.75} aria-hidden="true" />
            </span>
            <div className="min-w-0 flex-1">
              <p className="text-heading truncate text-sm font-semibold">{alert.title}</p>
              <p className="text-muted truncate text-[13px]">{alert.detail}</p>
            </div>
            <time className="text-muted shrink-0 text-xs" dateTime={occurredAt.toISOString()}>
              {formatRelativeTime(occurredAt, now)}
            </time>
          </li>
        );
      })}
    </ul>
  );
}
