import { ChevronRight, EllipsisVertical } from 'lucide-react';
import { Link } from 'react-router';
import type { QuickAction } from '../demo-data';

/** A list of jump-off points: tinted icon square, title, one-line description, chevron. */
export function QuickActions({ actions }: { actions: QuickAction[] }) {
  return (
    <ul className="divide-border divide-y">
      {actions.map((action) => (
        <li key={action.label}>
          <Link
            to={action.to}
            className="hover:bg-canvas -mx-3 flex items-center gap-3.5 rounded-control px-3 py-3.5 transition-colors"
          >
            <span className="bg-primary-soft flex size-10 shrink-0 items-center justify-center rounded-xl">
              <action.icon className="text-primary size-5" strokeWidth={1.75} aria-hidden="true" />
            </span>
            <span className="min-w-0 flex-1">
              <span className="text-heading block truncate text-sm font-semibold">{action.label}</span>
              <span className="text-muted block truncate text-[13px]">{action.description}</span>
            </span>
            <ChevronRight className="text-muted size-4 shrink-0" strokeWidth={1.75} aria-hidden="true" />
          </Link>
        </li>
      ))}

      <li>
        <Link
          to="/dashboards"
          className="hover:bg-canvas -mx-3 flex items-center gap-2 rounded-control px-3 py-3.5 transition-colors"
        >
          <span className="text-body flex-1 text-sm font-medium">More actions</span>
          <EllipsisVertical className="text-muted size-4" strokeWidth={1.75} aria-hidden="true" />
          <ChevronRight className="text-muted size-4 shrink-0" strokeWidth={1.75} aria-hidden="true" />
        </Link>
      </li>
    </ul>
  );
}
