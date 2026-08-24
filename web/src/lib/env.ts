/**
 * Browser-visible configuration. The AppHost supplies these to the Vite dev server
 * (`WebComposition.WithGridCoreWebAppConfiguration`); the defaults keep `npm run dev` usable
 * standalone. `VITE_` is the only prefix Vite exposes to the client bundle.
 */
export const env = {
  /** Keycloak realm URL — must be the same issuer the API validates against, or tokens are rejected. */
  oidcAuthority: import.meta.env.VITE_OIDC_AUTHORITY ?? 'http://localhost:8080/realms/gridcore',
  /** Public PKCE client registered in the realm. */
  oidcClientId: import.meta.env.VITE_OIDC_CLIENT_ID ?? 'gridcore-web',
  /** Audience the API expects on the access token. */
  oidcAudience: import.meta.env.VITE_OIDC_AUDIENCE ?? 'gridcore-api',
  /** API base path. Same-origin in dev: Vite proxies `/api` to the ASP.NET host. */
  apiBaseUrl: import.meta.env.VITE_API_BASE_URL ?? '',
  /** Organisation shown in the topbar switcher. */
  organizationName: import.meta.env.VITE_ORG_NAME ?? 'Rota Utilities',
} as const;
