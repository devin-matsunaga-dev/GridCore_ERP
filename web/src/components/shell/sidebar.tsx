import { NavLink } from 'react-router';
import { cn } from '@/lib/utils';
import { GridCoreMark } from './gridcore-mark';
import { navigation } from './navigation';
import { UserCard } from './user-card';

/** DESIGN.md shell: fixed dark-green sidebar, ~248px, logo top, grouped nav, user card pinned bottom. */
export function Sidebar({ className, onNavigate }: { className?: string; onNavigate?: () => void }) {
  return (
    <div className={cn('bg-sidebar flex h-full w-62 flex-col', className)}>
      <div className="flex items-center gap-2.5 px-5 pt-6 pb-5">
        <GridCoreMark />
        <span className="text-lg leading-none font-bold text-white">
          GridCore <span className="text-sidebar-text text-xs font-medium tracking-wide">ERP</span>
        </span>
      </div>

      <nav
        aria-label="Main"
        className="scrollbar-subtle scrollbar-on-sidebar flex-1 overflow-x-hidden overflow-y-auto px-3 pb-4"
      >
        {navigation.map((section, index) => (
          <div key={section.title ?? 'primary'} className={cn(index > 0 && 'mt-6')}>
            {section.title && (
              <h2 className="text-sidebar-section px-3 pb-2 text-[11px] font-semibold tracking-[0.08em] uppercase">
                {section.title}
              </h2>
            )}

            <ul className="space-y-0.5">
              {section.items.map((item) => (
                <li key={item.to}>
                  <NavLink
                    to={item.to}
                    end={item.to === '/'}
                    onClick={onNavigate}
                    className={({ isActive }) =>
                      cn(
                        'rounded-control relative flex items-center gap-3 px-3 py-2.5 text-sm font-medium transition-colors',
                        isActive
                          ? 'bg-sidebar-active text-sidebar-active-text'
                          : 'text-sidebar-text hover:bg-sidebar-active/60 hover:text-white',
                      )
                    }
                  >
                    {({ isActive }) => (
                      <>
                        {isActive && (
                          <span
                            className="bg-primary absolute top-2 bottom-2 -left-3 w-1 rounded-r-full"
                            aria-hidden="true"
                          />
                        )}
                        <item.icon className="size-5 shrink-0" strokeWidth={1.75} aria-hidden="true" />
                        <span className="truncate">{item.label}</span>
                      </>
                    )}
                  </NavLink>
                </li>
              ))}
            </ul>

            {section.title && index < navigation.length - 1 && (
              <div className="bg-sidebar-border mx-3 mt-6 h-px" aria-hidden="true" />
            )}
          </div>
        ))}
      </nav>

      <UserCard />
    </div>
  );
}
