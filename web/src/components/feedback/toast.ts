import { toast as sonnerToast } from 'sonner';
import { ApiError } from '@/api/client';

/**
 * The shared toast (CONVENTIONS.md: "errors via shared toast"). Components call these rather than
 * sonner directly, so an API failure always reads the same way — including the 403 the RBAC gate
 * returns, which is a permission message, not a crash.
 */
export const toast = {
  success: (message: string, description?: string) => sonnerToast.success(message, { description }),
  info: (message: string, description?: string) => sonnerToast.info(message, { description }),
  warning: (message: string, description?: string) => sonnerToast.warning(message, { description }),
  error: (message: string, description?: string) => sonnerToast.error(message, { description }),
  /** Turns any thrown value into the right message. */
  apiError: (error: unknown, fallback = 'Something went wrong.') =>
    sonnerToast.error(describeError(error, fallback)),
};

export function describeError(error: unknown, fallback = 'Something went wrong.'): string {
  if (error instanceof ApiError) {
    if (error.isUnreachable) return 'GridCore is not responding. Check that the API is running.';
    if (error.isForbidden) return error.problem?.detail ?? 'You do not have permission to do that.';
    if (error.isUnauthenticated) return 'Your session has expired. Sign in again to continue.';
    if (error.status === 404) return error.problem?.detail ?? 'That record no longer exists.';
    if (error.status === 409) return error.problem?.detail ?? 'That action conflicts with the current state.';
    return error.problem?.detail ?? error.problem?.title ?? fallback;
  }

  return error instanceof Error && error.message ? error.message : fallback;
}
