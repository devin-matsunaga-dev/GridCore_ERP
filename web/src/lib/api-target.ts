/**
 * Resolves the ASP.NET host the dev server proxies `/api` to. Node-side: imported by
 * `vite.config.ts`, never bundled into the browser.
 *
 * Aspire injects one variable per endpoint of every referenced resource, named
 * `services__<resource>__<scheme>__<index>`. For `web-host` that is currently http only.
 */
export const apiTargetPrefix = 'services__web-host__';

/** Where `/api` goes when Aspire has told us nothing — the Web.Host `http` launch profile. */
export const fallbackApiTarget = 'http://localhost:5273';

export function resolveApiTarget(env: Record<string, string | undefined>): {
  target: string;
  source: string;
} {
  const endpoints = Object.entries(env)
    .filter(([key, value]) => key.startsWith(apiTargetPrefix) && Boolean(value))
    .map(([key, value]) => ({ key, scheme: key.slice(apiTargetPrefix.length).split('__')[0], url: value! }));

  // https first when both are offered: the host redirects http to https in some profiles, and a
  // redirect is not something a proxied XHR follows usefully.
  const chosen = endpoints.find((e) => e.scheme === 'https') ?? endpoints.find((e) => e.scheme === 'http');
  if (chosen) return { target: chosen.url, source: chosen.key };

  if (env.VITE_API_URL) return { target: env.VITE_API_URL, source: 'VITE_API_URL' };

  return { target: fallbackApiTarget, source: 'fallback (no Aspire service discovery variables)' };
}
