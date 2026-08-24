import type * as React from 'react';
import { statusToneClasses, type StatusTone } from '@/components/ui/status';
import { formatDateTime } from '@/lib/format';
import { cn } from '@/lib/utils';

/**
 * The history read models rendered as one timeline — a service account's transitions, an asset's
 * register/inspection/maintenance lines. One shared component because both answer the same
 * question in the same shape: what happened, why, who did it and when.
 *
 * Newest first, which is the order both APIs' detail responses and the screens agree on.
 */

export type TimelineEntry = {
  id: string;
  title: React.ReactNode;
  /** The reason or note. */
  detail?: React.ReactNode;
  /** Who recorded it — captured alongside the subject id, because directory entries do not last. */
  actor?: string | null;
  recordedAt: string;
  tone?: StatusTone;
};

export function Timeline({ entries, className }: { entries: readonly TimelineEntry[]; className?: string }) {
  return (
    <ol className={cn('space-y-0', className)}>
      {entries.map((entry, index) => (
        <li key={entry.id} className="relative flex gap-3 pb-5 last:pb-0">
          {/* The rail, drawn between dots and stopped short on the last entry so it does not dangle. */}
          {index < entries.length - 1 && (
            <span className="bg-border absolute top-4 bottom-0 left-[3.5px] w-px" aria-hidden="true" />
          )}
          <span
            className={cn(
              'relative mt-1.5 size-2 shrink-0 rounded-full',
              statusToneClasses(entry.tone ?? 'neutral').dot,
            )}
            aria-hidden="true"
          />
          <div className="min-w-0 flex-1">
            <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-0.5">
              <p className="text-heading text-[13px] font-medium">{entry.title}</p>
              <time className="text-muted shrink-0 text-xs" dateTime={entry.recordedAt}>
                {formatDateTime(entry.recordedAt)}
              </time>
            </div>
            {entry.detail && <p className="text-body mt-1 text-[13px]">{entry.detail}</p>}
            {entry.actor && <p className="text-muted mt-1 text-xs">{entry.actor}</p>}
          </div>
        </li>
      ))}
    </ol>
  );
}
