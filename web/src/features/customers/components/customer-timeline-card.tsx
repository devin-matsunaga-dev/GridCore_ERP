import { History } from 'lucide-react';
import type { Column } from '@/components/registry/data-table';
import { EmptyState } from '@/components/registry/empty-state';
import { RegistryTableCard } from '@/components/registry/registry-table-card';
import { useTableState } from '@/components/registry/table-state';
import { StatusDot } from '@/components/ui/status';
import { formatDate, formatDateTime } from '@/lib/format';
import type { CustomerTimelineEntry } from '../customer-360';

/**
 * Everything that has happened to this customer, from four modules, in one reverse-chronological
 * table: accounts opened and moved, bills issued, corrections made, payments taken and refused.
 *
 * A TABLE rather than a dotted rail, unlike the per-account history in the drawer. One account's
 * transitions are a handful of lines about a single subject, which is what `Timeline` is for; this
 * is years of uniform four-field records from four modules, which is a register a rep scans and
 * sorts. The tone survives the change as the dot beside each event, so a decline still reads red.
 *
 * The merge is not here — it is `buildCustomerTimeline`, which is pure and where the ordering is
 * tested. This card only renders it, and `useTableState` pages it.
 *
 * Its loading and error state is the WEAKEST of the panels it draws from, which is the honest
 * reading: a feed missing one module's entries is not a shorter feed, it is a wrong one, and a rep
 * cannot tell which by looking. Every other tab still loads and fails on its own.
 */
export function CustomerTimelineCard({
  entries,
  isLoading,
  error,
  onRetry,
}: {
  entries: readonly CustomerTimelineEntry[];
  isLoading: boolean;
  error: unknown;
  onRetry: () => void;
}) {
  // No initial sort: the rows arrive in the total order `buildCustomerTimeline` put them in, which
  // is newest-first and already breaks ties the way a re-sort here could not (kind, then id).
  const table = useTableState({ rows: entries, columns: timelineColumns, initialPageSize: 25 });

  return (
    <RegistryTableCard
      columns={timelineColumns}
      table={table}
      rowKey={(entry) => entry.id}
      label="Account timeline"
      isLoading={isLoading}
      error={error}
      onRetry={onRetry}
      empty={
        <EmptyState
          icon={History}
          title="Nothing has happened yet"
          message="Opening an account, issuing a bill or taking a payment all land here, in the order they happened."
        />
      }
    />
  );
}

const timelineColumns: Column<CustomerTimelineEntry>[] = [
  {
    key: 'occurredAt',
    header: 'When',
    sortValue: (entry) => entry.occurredAt,
    // A bill is issued on a `DateOnly`; rendering the midnight it parses to as "12:00 AM" would
    // state a time the utility never recorded.
    cell: (entry) => (
      <time className="text-muted text-xs" dateTime={entry.occurredAt}>
        {entry.precision === 'date' ? formatDate(entry.occurredAt) : formatDateTime(entry.occurredAt)}
      </time>
    ),
  },
  {
    key: 'title',
    header: 'Event',
    wide: true,
    sortValue: (entry) => entry.title,
    cell: (entry) => (
      <StatusDot tone={entry.tone} className="min-w-0 items-start">
        <span className="block min-w-0">
          <span className="text-heading block font-medium">{entry.title}</span>
          {entry.detail && <span className="text-muted block text-xs">{entry.detail}</span>}
        </span>
      </StatusDot>
    ),
  },
  {
    key: 'kind',
    header: 'Source',
    sortValue: (entry) => entry.kind,
    cell: (entry) => <span className="text-muted text-xs">{sourceLabels[entry.kind]}</span>,
  },
  {
    key: 'actor',
    header: 'Who',
    sortValue: (entry) => entry.actor,
    cell: (entry) => <span className="text-muted truncate text-xs">{entry.actor ?? '—'}</span>,
  },
];

/** Which module an entry came from, in a word. The column is what makes the merge legible. */
const sourceLabels: Record<CustomerTimelineEntry['kind'], string> = {
  account: 'Account',
  bill: 'Billing',
  adjustment: 'Adjustment',
  payment: 'Payment',
  note: 'Note',
};
