import { StatusDot, StatusPill } from '@/components/ui/status';
import { cn } from '@/lib/utils';
import type { WorkOrderRow } from '../demo-data';

const headings = ['ID', 'Type', 'Description', 'Status', 'Priority', 'Created'] as const;

/** DESIGN.md density: compact rows, muted 11px headers, no zebra, row-hover tint, muted ID column. */
export function WorkOrderTable({ rows }: { rows: WorkOrderRow[] }) {
  return (
    <div className="-mx-6 overflow-x-auto px-6">
      <table className="w-full min-w-[20rem] border-collapse text-sm">
        <thead>
          <tr className="border-border border-b">
            {headings.map((heading) => (
              <th
                key={heading}
                scope="col"
                // Description absorbs the slack so it truncates last, not first.
                className={cn(
                  'text-muted px-1 pb-2.5 text-left text-[11px] font-medium tracking-[0.06em] whitespace-nowrap uppercase first:pl-0 last:pr-0 last:text-right',
                  heading === 'Description' && 'w-full',
                )}
              >
                {heading}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={row.id} className="border-border hover:bg-canvas border-b transition-colors last:border-0">
              <td className="text-heading tabular px-1 py-2.5 pl-0 text-xs font-semibold whitespace-nowrap">
                {row.id}
              </td>
              <td className="text-body px-1 py-2.5 text-xs whitespace-nowrap">{row.type}</td>
              <td className="text-body max-w-0 truncate px-1 py-2.5 text-xs" title={row.description}>
                {row.description}
              </td>
              <td className="px-1 py-2.5">
                <StatusDot status={row.status} className="text-body text-xs whitespace-nowrap" />
              </td>
              <td className="px-1 py-2.5">
                <StatusPill status={row.priority} />
              </td>
              <td className="text-muted tabular px-1 py-2.5 pr-0 text-right text-xs whitespace-nowrap">
                {row.createdAt}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
