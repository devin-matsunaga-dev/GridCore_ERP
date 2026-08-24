import { Lock, TriangleAlert } from 'lucide-react';
import { ApiError } from '@/api/client';
import { Button } from '@/components/ui/button';
import { describeError } from '@/components/feedback/toast';
import { cn } from '@/lib/utils';

/**
 * What a registry shows when its query failed. A 403 is not a crash — it is the RBAC gate doing its
 * job, and it reads as a permission message with no retry button, because retrying repeats it.
 * Everything else is offered a retry.
 */
export function ErrorState({
  error,
  onRetry,
  className,
}: {
  error: unknown;
  onRetry?: () => void;
  className?: string;
}) {
  const forbidden = error instanceof ApiError && error.isForbidden;
  const Icon = forbidden ? Lock : TriangleAlert;

  return (
    <div
      role="alert"
      className={cn('flex flex-col items-center justify-center px-6 py-14 text-center', className)}
    >
      <span
        className={cn(
          'flex size-12 items-center justify-center rounded-full',
          forbidden ? 'bg-neutral-soft' : 'bg-danger-soft',
        )}
      >
        <Icon
          className={cn('size-6', forbidden ? 'text-neutral' : 'text-danger')}
          strokeWidth={1.5}
          aria-hidden="true"
        />
      </span>
      <h3 className="text-heading mt-4 text-[15px] font-semibold">
        {forbidden ? 'You do not have access to this' : 'That did not load'}
      </h3>
      <p className="text-body mt-1.5 max-w-sm text-[13px]">{describeError(error)}</p>
      {!forbidden && onRetry && (
        <Button variant="secondary" className="mt-5" onClick={onRetry}>
          Try again
        </Button>
      )}
    </div>
  );
}
