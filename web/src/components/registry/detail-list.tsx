import type * as React from 'react';
import { cn } from '@/lib/utils';

/**
 * The label/value pairs a detail drawer and the 360° page are built from — DESIGN.md's description
 * list, with the labels at 13/500 muted and the values in body colour. A value that is not recorded
 * renders as an em dash rather than an empty gap, so a blank row still reads as a row.
 */

export type DetailItem = {
  label: string;
  value: React.ReactNode;
  /** Spans both columns — an address, a note, a reason. */
  wide?: boolean;
};

export function DetailList({
  items,
  columns = 2,
  className,
}: {
  items: readonly DetailItem[];
  columns?: 1 | 2;
  className?: string;
}) {
  return (
    <dl
      className={cn(
        'grid gap-x-6 gap-y-4',
        columns === 2 ? 'grid-cols-[repeat(2,minmax(0,1fr))]' : 'grid-cols-[minmax(0,1fr)]',
        className,
      )}
    >
      {items.map((item) => (
        <div key={item.label} className={cn('min-w-0', item.wide && columns === 2 && 'col-span-2')}>
          <dt className="text-muted text-[11px] font-medium tracking-[0.06em] uppercase">{item.label}</dt>
          <dd className="text-body mt-1 text-[13px] break-words">{item.value ?? <NotRecorded />}</dd>
        </div>
      ))}
    </dl>
  );
}

/** The one rendering of "no value here", so a drawer never shows a stray blank. */
export function NotRecorded() {
  return <span className="text-muted">—</span>;
}

/** `value ?? <NotRecorded />`, for the many nullable columns these registries carry. */
export function orNotRecorded(value: React.ReactNode): React.ReactNode {
  return value === null || value === undefined || value === '' ? <NotRecorded /> : value;
}
