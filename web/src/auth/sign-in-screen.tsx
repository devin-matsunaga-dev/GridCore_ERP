import { ShieldAlert } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { GridCoreMark } from '@/components/shell/gridcore-mark';

export type SignInScreenProps =
  | { state: 'redirecting'; message?: undefined; onRetry?: undefined }
  | { state: 'error'; message: string; onRetry: () => void };

/** Shown while the browser is on its way to Keycloak, and when it comes back with a failure. */
export function SignInScreen({ state, message, onRetry }: SignInScreenProps) {
  return (
    <div className="bg-canvas flex min-h-dvh items-center justify-center p-6">
      <div className="bg-card border-border rounded-card shadow-card w-full max-w-sm border p-8 text-center">
        <div className="flex justify-center">
          {state === 'error' ? (
            <span className="bg-danger-soft flex size-12 items-center justify-center rounded-full">
              <ShieldAlert className="text-danger size-6" strokeWidth={1.75} />
            </span>
          ) : (
            <GridCoreMark className="size-12" />
          )}
        </div>

        <h1 className="text-heading mt-5 text-xl font-bold">
          {state === 'error' ? 'Sign-in failed' : 'Signing you in'}
        </h1>
        <p className="text-body mt-2 text-sm">
          {state === 'error' ? message : 'Redirecting to GridCore identity…'}
        </p>

        {state === 'error' && (
          <Button className="mt-6 w-full" onClick={onRetry}>
            Try again
          </Button>
        )}
      </div>
    </div>
  );
}
