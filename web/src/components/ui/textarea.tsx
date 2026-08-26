import type * as React from 'react';
import { cn } from '@/lib/utils';

/**
 * A multi-line field, styled as `Input`'s sibling.
 *
 * The first place GridCore needs one is the customer note log (WP-2.13): a rep typing an account of
 * a telephone call into a single-line input can only see the last few words of what they wrote,
 * which is how notes end up terse. Every token is shared with `Input` — border, radius, focus ring,
 * invalid state — so the two cannot drift apart in a form that holds both.
 *
 * `field-sizing-content` lets the box grow with what is typed where the browser supports it, and
 * `min-h` is what it falls back to where it does not. `resize-y` because a rep who wants more room
 * than that should have it, and a horizontally resizable field breaks the form's column.
 */
export function Textarea({ className, ...props }: React.ComponentProps<'textarea'>) {
  return (
    <textarea
      data-slot="textarea"
      className={cn(
        'border-border bg-card text-heading placeholder:text-muted rounded-field field-sizing-content w-full resize-y border px-3 py-2 text-sm',
        'min-h-20',
        'focus-visible:border-primary focus-visible:ring-ring/30 transition-[color,box-shadow] focus-visible:ring-[3px] focus-visible:outline-none',
        'aria-invalid:border-danger disabled:cursor-not-allowed disabled:opacity-50',
        className,
      )}
      {...props}
    />
  );
}
