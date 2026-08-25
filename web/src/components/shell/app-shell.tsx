import { X } from 'lucide-react';
import { useState } from 'react';
import { Outlet, useLocation } from 'react-router';
import { Button } from '@/components/ui/button';
import { Sidebar } from './sidebar';
import { Topbar } from './topbar';

/**
 * The application frame: a fixed sidebar at full height, and a scrolling content column holding
 * the topbar and the routed page. Below `lg` the sidebar becomes an overlay drawer so
 * the shell still works at the 1280px floor and under.
 */
export function AppShell() {
  const [isNavigationOpen, setNavigationOpen] = useState(false);
  const location = useLocation();

  return (
    <div className="bg-canvas flex h-dvh overflow-hidden">
      <aside className="hidden shrink-0 lg:block">
        <Sidebar className="h-full" />
      </aside>

      {isNavigationOpen && (
        <div className="fixed inset-0 z-50 lg:hidden">
          <button
            type="button"
            className="absolute inset-0 bg-black/40"
            aria-label="Close navigation"
            onClick={() => setNavigationOpen(false)}
          />
          <div className="relative h-full w-62">
            <Sidebar className="h-full" onNavigate={() => setNavigationOpen(false)} />
            <Button
              variant="ghost"
              size="iconSm"
              className="text-sidebar-text absolute top-4 -right-11 hover:bg-white/10 hover:text-white"
              onClick={() => setNavigationOpen(false)}
              aria-label="Close navigation"
            >
              <X aria-hidden="true" />
            </Button>
          </div>
        </div>
      )}

      {/*
        `relative` is load-bearing, not decoration. Without a positioned ancestor, an absolutely
        positioned descendant — every `.sr-only` label and table caption in the app — has the
        INITIAL containing block, so it is not clipped by the shell's `overflow-hidden` and it
        extends the DOCUMENT's scrollable height to its own unscrolled flow position. On a page
        taller than the viewport that shows up as a browser scrollbar and a band of blank white
        below the shell. Positioning this column makes it the containing block, so those boxes
        belong to the pane that is meant to scroll.
      */}
      <div className="scrollbar-subtle relative flex min-w-0 flex-1 flex-col overflow-x-hidden overflow-y-auto">
        <Topbar onOpenNavigation={() => setNavigationOpen(true)} notificationCount={3} />

        <main key={location.pathname} className="flex-1 p-6">
          <Outlet />
        </main>

      </div>
    </div>
  );
}
