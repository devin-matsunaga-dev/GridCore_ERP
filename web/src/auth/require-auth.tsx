import { useEffect } from 'react';
import { useAuth } from 'react-oidc-context';
import { SignInScreen } from './sign-in-screen';

/**
 * The protected-route gate. An unauthenticated visitor is sent to Keycloak rather than shown an
 * empty shell; a failed sign-in surfaces the error with a retry instead of looping the redirect.
 */
export function RequireAuth({ children }: { children: React.ReactNode }) {
  const auth = useAuth();
  const { isAuthenticated, isLoading, activeNavigator, error, signinRedirect } = auth;

  const shouldRedirect = !isAuthenticated && !isLoading && !activeNavigator && !error;

  useEffect(() => {
    if (shouldRedirect) {
      void signinRedirect();
    }
  }, [shouldRedirect, signinRedirect]);

  if (error) {
    return <SignInScreen state="error" message={error.message} onRetry={() => void signinRedirect()} />;
  }

  if (!isAuthenticated) {
    return <SignInScreen state="redirecting" />;
  }

  return children;
}
