import { cn } from '@/lib/utils';

/** The hexagonal grid mark from docs/Design.png. */
export function GridCoreMark({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 32 32" fill="none" className={cn('size-8', className)} aria-hidden="true">
      <path
        d="M16 3.5 27.5 10v12L16 28.5 4.5 22V10L16 3.5Z"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinejoin="round"
        className="text-primary"
      />
      <path d="M16 11.5 21 14.5v5L16 22.5 11 19.5v-5L16 11.5Z" fill="currentColor" className="text-primary" />
    </svg>
  );
}
