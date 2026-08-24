import type * as React from 'react';
import { cn } from '@/lib/utils';

/** The five semantic tones from DESIGN.md. Every state machine's status maps onto one of these. */
export type StatusTone = 'success' | 'warning' | 'danger' | 'info' | 'neutral';

const toneClasses: Record<StatusTone, { pill: string; dot: string; text: string; soft: string }> = {
  success: { pill: 'bg-success-soft text-success', dot: 'bg-success', text: 'text-success', soft: 'bg-success-soft' },
  warning: { pill: 'bg-warning-soft text-warning', dot: 'bg-warning', text: 'text-warning', soft: 'bg-warning-soft' },
  danger: { pill: 'bg-danger-soft text-danger', dot: 'bg-danger', text: 'text-danger', soft: 'bg-danger-soft' },
  info: { pill: 'bg-info-soft text-info', dot: 'bg-info', text: 'text-info', soft: 'bg-info-soft' },
  neutral: { pill: 'bg-neutral-soft text-neutral', dot: 'bg-neutral', text: 'text-neutral', soft: 'bg-neutral-soft' },
};

/**
 * The shared semantic map. Statuses are matched case-insensitively so a module may hand over its
 * own enum name (`InProgress`) or a display label ("In Progress") and get the same tone.
 * An unmapped status is neutral — never an unstyled or missing pill.
 */
const toneByStatus: Record<string, StatusTone> = {
  // Health / connectivity
  online: 'success',
  offline: 'danger',
  outage: 'danger',
  maintenance: 'neutral',
  // Work
  completed: 'success',
  complete: 'success',
  active: 'success',
  inprogress: 'info',
  scheduled: 'warning',
  pending: 'warning',
  onhold: 'neutral',
  cancelled: 'neutral',
  closed: 'neutral',
  draft: 'neutral',
  // Money
  paid: 'success',
  approved: 'success',
  issued: 'info',
  overdue: 'danger',
  declined: 'danger',
  rejected: 'danger',
  disconnected: 'danger',
  // Registry lifecycles (WP-1.5): the customer, service-account, asset and stock-item statuses.
  // A lifecycle state gets the tone of what it means operationally, not of how it sounds.
  prospect: 'info',
  suspended: 'warning',
  instorage: 'neutral',
  inservice: 'success',
  undermaintenance: 'warning',
  retired: 'neutral',
  discontinued: 'neutral',
  lowstock: 'danger',
  instock: 'success',
  // Asset condition — the inspector's grade, worst two both read as something to act on.
  unknown: 'neutral',
  excellent: 'success',
  good: 'success',
  fair: 'warning',
  poor: 'danger',
  critical: 'danger',
  // Meter lifecycle (WP-2.1). Installed is the only status a bill may be raised from, so it reads
  // as the good one; Faulty is still on the wall and still measuring badly, which is the danger.
  // `instore` is the meter's own word and deliberately not `instock`, which above already means
  // "this catalogue line is stocked and above its reorder level" — the same collision the low-stock
  // pill avoided by never being labelled "Low".
  instore: 'neutral',
  installed: 'success',
  faulty: 'danger',
  removed: 'neutral',
  // Stock movements: what arrived, what left, and the one that moved a count with nothing moving.
  receipt: 'success',
  issue: 'info',
  adjustment: 'warning',
  // Priority
  high: 'danger',
  medium: 'warning',
  low: 'success',
  // Generic
  warning: 'warning',
  info: 'info',
  error: 'danger',
  success: 'success',
};

/** Normalises "In Progress", "in-progress" and `InProgress` to the same key. */
export function toneFor(status: string): StatusTone {
  return toneByStatus[status.toLowerCase().replace(/[\s_-]/g, '')] ?? 'neutral';
}

export function statusToneClasses(tone: StatusTone) {
  return toneClasses[tone];
}

export type StatusPillProps = React.ComponentProps<'span'> & {
  status: string;
  tone?: StatusTone;
};

/** DESIGN.md: soft bg + semantic text, 6px radius, no border, 12px/500. */
export function StatusPill({ status, tone, className, ...props }: StatusPillProps) {
  const resolved = tone ?? toneFor(status);
  return (
    <span
      data-slot="status-pill"
      className={cn(
        'rounded-pill inline-flex items-center px-2 py-0.5 text-xs font-medium',
        toneClasses[resolved].pill,
        className,
      )}
      {...props}
    >
      {status}
    </span>
  );
}

export type StatusDotProps = React.ComponentProps<'span'> & {
  status?: string;
  tone?: StatusTone;
  label?: React.ReactNode;
};

/** Filled circle + label — the System Overview legend and the work-order feed. */
export function StatusDot({ status, tone, label, className, children, ...props }: StatusDotProps) {
  const resolved = tone ?? (status ? toneFor(status) : 'neutral');
  return (
    <span data-slot="status-dot" className={cn('inline-flex items-center gap-2', className)} {...props}>
      <span className={cn('size-2 shrink-0 rounded-full', toneClasses[resolved].dot)} aria-hidden="true" />
      {label ?? children ?? status}
    </span>
  );
}
