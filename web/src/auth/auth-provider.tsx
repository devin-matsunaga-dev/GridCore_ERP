import { useEffect } from 'react';
import { AuthProvider as OidcAuthProvider, useAuth } from 'react-oidc-context';
import { clearAccessTokenProvider, setAccessTokenProvider } from '@/api/client';
import { buildOidcConfig } from './oidc-config';

/**
 * Hands the API client a way to read the current access token. Lives inside the OIDC provider so
 * the client itself stays React-free and testable.
 */
function AuthTokenBridge({ children }: { children: React.ReactNode }) {
  const auth = useAuth();

  // Assigned during render, not from an effect. A child's mount effect runs *before* its parent's,
  // so an effect here would let the shell's first `/api/me` leave without an Authorization header
  // and come back 401 — which the query client will not retry, because a 401 is not transient.
  setAccessTokenProvider(() => auth.user?.access_token);

  // Only the teardown needs an effect: once this provider unmounts, nothing should still be
  // handing out a token.
  useEffect(() => clearAccessTokenProvider, []);

  return children;
}

/** Strips the `?code=…&state=…` the identity provider appends, so a refresh does not replay it. */
function clearSigninQuery() {
  window.history.replaceState({}, document.title, window.location.pathname);
}

export function AuthProvider({ children }: { children: React.ReactNode }) {
  return (
    <OidcAuthProvider {...buildOidcConfig(clearSigninQuery)}>
      <AuthTokenBridge>{children}</AuthTokenBridge>
    </OidcAuthProvider>
  );
}
