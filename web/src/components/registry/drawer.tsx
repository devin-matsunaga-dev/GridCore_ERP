import { X } from 'lucide-react';
import { useEffect, useId, useRef } from 'react';
import type * as React from 'react';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';

/**
 * The detail drawer: a panel that slides in from the right over the registry table, so a row can be
 * inspected without losing the list, the filters or the scroll position behind it.
 *
 * Hand-written rather than a new dependency (CONVENTIONS.md: no new libraries without asking) —
 * `@radix-ui/react-dialog` is not in the project and this needs an overlay, Escape, a focus loop
 * and a scroll lock, which is what is below.
 */

export type DrawerProps = {
  open: boolean;
  onClose: () => void;
  title: React.ReactNode;
  /** Sits under the title in the header — a registry number, a status pill. */
  subtitle?: React.ReactNode;
  /** Pinned to the bottom of the panel, outside the scrolling body. */
  footer?: React.ReactNode;
  children: React.ReactNode;
  className?: string;
};

/** Everything inside the panel that can hold focus, in document order. */
const focusableSelector =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

export function Drawer({ open, onClose, title, subtitle, footer, children, className }: DrawerProps) {
  const panelRef = useRef<HTMLDivElement>(null);
  const titleId = useId();

  useEffect(() => {
    if (!open) return;

    // Restored on close: dismissing the drawer must put the caret back on the row that opened it,
    // or a keyboard user lands at the top of the document and has to walk the table again.
    const previouslyFocused = document.activeElement as HTMLElement | null;
    panelRef.current?.focus();

    const { overflow } = document.body.style;
    document.body.style.overflow = 'hidden';

    function onKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') {
        event.stopPropagation();
        onClose();
        return;
      }

      if (event.key !== 'Tab' || !panelRef.current) return;

      const focusable = [...panelRef.current.querySelectorAll<HTMLElement>(focusableSelector)];
      if (focusable.length === 0) {
        event.preventDefault();
        panelRef.current.focus();
        return;
      }

      const first = focusable[0]!;
      const last = focusable[focusable.length - 1]!;
      const active = document.activeElement;

      // Wrap at both ends, so Tab cannot walk out of a modal panel into the page behind it.
      if (event.shiftKey && (active === first || active === panelRef.current)) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && active === last) {
        event.preventDefault();
        first.focus();
      }
    }

    document.addEventListener('keydown', onKeyDown, true);

    return () => {
      document.removeEventListener('keydown', onKeyDown, true);
      document.body.style.overflow = overflow;
      previouslyFocused?.focus?.();
    };
  }, [open, onClose]);

  if (!open) return null;

  return (
    <div className="fixed inset-0 z-50 flex justify-end">
      <button
        type="button"
        aria-label="Close details"
        onClick={onClose}
        className="absolute inset-0 bg-black/30 backdrop-blur-[1px]"
      />

      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
        className={cn(
          'bg-card border-border relative flex h-full w-full max-w-[34rem] flex-col border-l shadow-xl outline-none',
          'animate-in slide-in-from-right duration-200',
          className,
        )}
      >
        <header className="border-border flex items-start justify-between gap-4 border-b px-6 py-5">
          <div className="min-w-0">
            <h2 id={titleId} className="text-heading truncate text-lg leading-tight font-semibold">
              {title}
            </h2>
            {subtitle && <div className="mt-1.5 flex flex-wrap items-center gap-2">{subtitle}</div>}
          </div>
          <Button variant="ghost" size="iconSm" onClick={onClose} aria-label="Close details">
            <X aria-hidden="true" />
          </Button>
        </header>

        <div className="scrollbar-subtle flex-1 overflow-y-auto px-6 py-5">{children}</div>

        {footer && <div className="border-border bg-canvas border-t px-6 py-4">{footer}</div>}
      </div>
    </div>
  );
}

/** A titled block inside a drawer or a detail page. */
export function DrawerSection({
  title,
  action,
  children,
  className,
}: {
  title: string;
  action?: React.ReactNode;
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <section className={cn('border-border border-t pt-5 first:border-0 first:pt-0', className)}>
      <div className="mb-3.5 flex items-center justify-between gap-3">
        <h3 className="text-heading text-[13px] font-semibold">{title}</h3>
        {action}
      </div>
      {children}
    </section>
  );
}
