import { WebStorageStateStore } from 'oidc-client-ts';
import type { AuthProviderProps } from 'react-oidc-context';
import { env } from '@/lib/env';

/**
 * Authorization Code + PKCE against the realm's public `gridcore-web` client. The authority is
 * whatever the AppHost resolved Keycloak to, which is also the issuer the API validates — using a
 * different host for login than for validation would fail issuer matching.
 */
export function buildOidcConfig(
  onSigninCallback: AuthProviderProps['onSigninCallback'],
): AuthProviderProps {
  return {
    authority: env.oidcAuthority,
    client_id: env.oidcClientId,
    redirect_uri: `${window.location.origin}/`,
    post_logout_redirect_uri: `${window.location.origin}/`,
    response_type: 'code',
    // `openid profile email` identifies the user; the audience scope is what makes the access
    // token usable against the API.
    scope: 'openid profile email',
    // Silent renew keeps a long demo session alive without bouncing through the login page.
    automaticSilentRenew: true,
    // Tokens live in sessionStorage: closing the tab ends the session, and nothing survives in
    // localStorage for another script on the origin to read.
    userStore: new WebStorageStateStore({ store: window.sessionStorage }),
    onSigninCallback,
  };
}
