import { NavLink } from 'react-router';
import { cn } from '@/lib/utils';

/**
 * A segmented control of routes — the two faces of the Customers area, and whatever a later
 * registry needs to sit side by side. Links rather than local state, so a tab is shareable, the
 * back button works, and the sidebar's Customers entry stays active across both.
 */

export type TabNavItem = {
  label: string;
  to: string;
  /** Match this route exactly. The index tab needs it or every child route lights it up too. */
  end?: boolean;
};

export function TabNav({ items, className }: { items: readonly TabNavItem[]; className?: string }) {
  return (
    <nav className={cn('border-border bg-card rounded-control inline-flex gap-1 border p-1', className)}>
      {items.map((item) => (
        <NavLink
          key={item.to}
          to={item.to}
          end={item.end}
          className={({ isActive }) =>
            cn(
              'rounded-field px-3 py-1.5 text-[13px] font-medium transition-colors',
              isActive ? 'bg-primary-soft text-primary' : 'text-body hover:bg-canvas hover:text-heading',
            )
          }
        >
          {item.label}
        </NavLink>
      ))}
    </nav>
  );
}
