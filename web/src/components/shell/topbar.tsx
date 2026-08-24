import { Bell, ChevronDown, CircleQuestionMark, Menu, Search } from 'lucide-react';
import { useEffect, useRef } from 'react';
import { useCurrentUser } from '@/api/identity';
import { Button } from '@/components/ui/button';
import { Separator } from '@/components/ui/separator';
import { env } from '@/lib/env';
import { cn } from '@/lib/utils';

/** "Good morning" / "Good afternoon" / "Good evening", from the viewer's own clock. */
export function greetingFor(now: Date): string {
  const hour = now.getHours();
  if (hour < 12) return 'Good morning';
  if (hour < 18) return 'Good afternoon';
  return 'Good evening';
}

/** First name for the greeting; an email falls back to the part before the `@`. */
export function firstNameOf(displayName: string): string {
  const base = displayName.includes('@') ? displayName.split('@')[0]! : displayName;
  return base.split(/[\s._-]+/).filter(Boolean)[0] ?? base;
}

/** Detects the platform so the search hint shows ⌘K on macOS and Ctrl K elsewhere. */
function isAppleDevice(): boolean {
  return typeof navigator !== 'undefined' && /Mac|iPhone|iPad/i.test(navigator.platform || navigator.userAgent);
}

export type TopbarProps = {
  onOpenNavigation: () => void;
  notificationCount?: number;
  className?: string;
};

/** DESIGN.md topbar, in the content column: greeting + subline left, search centre-right, actions right. */
export function Topbar({ onOpenNavigation, notificationCount = 0, className }: TopbarProps) {
  const { data } = useCurrentUser();
  const searchRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key.toLowerCase() === 'k' && (event.metaKey || event.ctrlKey)) {
        event.preventDefault();
        searchRef.current?.focus();
      }
    };

    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, []);

  const name = firstNameOf(data?.userName ?? data?.email ?? 'there');

  return (
    <header className={cn('bg-card border-border border-b px-6 py-4', className)}>
      <div className="flex flex-wrap items-center gap-4">
        <Button
          variant="ghost"
          size="icon"
          className="lg:hidden"
          onClick={onOpenNavigation}
          aria-label="Open navigation"
        >
          <Menu aria-hidden="true" />
        </Button>

        <div className="min-w-0 flex-1">
          <h1 className="text-heading truncate text-[26px] leading-tight font-bold">
            {greetingFor(new Date())}, {name}.
          </h1>
          <p className="text-muted mt-0.5 truncate text-sm">Here's what's happening across the grid.</p>
        </div>

        <search className="order-last w-full min-w-0 md:order-none md:w-72 xl:w-96">
          <div className="relative">
            <Search
              className="text-muted pointer-events-none absolute top-1/2 left-3.5 size-[18px] -translate-y-1/2"
              strokeWidth={1.75}
              aria-hidden="true"
            />
            <input
              ref={searchRef}
              type="search"
              placeholder="Search GridCore"
              aria-label="Search GridCore"
              className="border-border bg-canvas text-heading placeholder:text-muted focus-visible:border-primary focus-visible:ring-ring/25 h-11 w-full rounded-full border pr-16 pl-11 text-sm focus-visible:ring-[3px] focus-visible:outline-none"
            />
            <kbd className="border-border text-muted bg-card pointer-events-none absolute top-1/2 right-3 -translate-y-1/2 rounded border px-1.5 py-0.5 text-[11px] font-medium">
              {isAppleDevice() ? '⌘' : 'Ctrl'} K
            </kbd>
          </div>
        </search>

        <div className="flex shrink-0 items-center gap-1">
          <Button variant="ghost" size="icon" className="relative" aria-label={`Notifications (${notificationCount})`}>
            <Bell aria-hidden="true" />
            {notificationCount > 0 && (
              <span className="bg-danger absolute top-1 right-1 flex size-4 items-center justify-center rounded-full text-[10px] font-semibold text-white">
                {notificationCount > 9 ? '9+' : notificationCount}
              </span>
            )}
          </Button>

          <Button variant="ghost" size="icon" aria-label="Help">
            <CircleQuestionMark aria-hidden="true" />
          </Button>

          <Separator orientation="vertical" className="mx-2 hidden sm:block" />

          <button
            type="button"
            className="rounded-control hover:bg-canvas hidden items-center gap-2.5 px-2.5 py-2 transition-colors sm:flex"
            aria-label={`Organisation: ${env.organizationName}`}
          >
            <OrgMark />
            <span className="text-heading text-sm font-semibold">{env.organizationName}</span>
            <ChevronDown className="text-muted size-4" strokeWidth={1.75} aria-hidden="true" />
          </button>
        </div>
      </div>
    </header>
  );
}

/** The scattered-dot org glyph from the reference screenshot. */
function OrgMark() {
  return (
    <svg viewBox="0 0 20 20" className="size-5" aria-hidden="true">
      <circle cx="10" cy="4" r="1.6" className="fill-chart-1" />
      <circle cx="16" cy="8" r="1.6" className="fill-chart-2" />
      <circle cx="14" cy="15" r="1.6" className="fill-chart-3" />
      <circle cx="6" cy="15" r="1.6" className="fill-chart-4" />
      <circle cx="4" cy="8" r="1.6" className="fill-chart-1/60" />
      <circle cx="10" cy="10" r="1.9" className="fill-chart-1" />
    </svg>
  );
}
