import { fileURLToPath, URL } from 'node:url';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { defineConfig } from 'vitest/config';
import { resolveApiTarget } from './src/lib/api-target';

/**
 * The Keycloak realm registers the SPA at `http://localhost:5173`, and the AppHost pins the
 * resource to that port too (`WebComposition.WebAppPort`). A drifting port is a rejected
 * `redirect_uri`, not a warning — all three have to agree.
 */
const DEV_PORT = 5173;

export default defineConfig(({ mode, command }) => {
  const { target, source } = resolveApiTarget(process.env);

  if (command === 'serve' && !process.env.VITEST) {
    // Printed on every dev start: where `/api` goes is the first thing to check when the SPA
    // loads but every request fails, and it is otherwise invisible.
    console.info(`[gridcore] proxying /api → ${target}  (from ${source})`);
    console.info(`[gridcore] OIDC authority: ${process.env.VITE_OIDC_AUTHORITY ?? '(not set — using the default)'}`);
  }

  return {
    plugins: [react(), tailwindcss()],
    resolve: {
      alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
    },
    server: {
      port: DEV_PORT,
      strictPort: true,
      // Same-origin `/api` in the browser, so the host needs no CORS policy.
      proxy: {
        '/api': {
          target,
          changeOrigin: true,
          secure: false,
          configure: (proxy) => {
            // Without this a proxy failure is silent: the browser request simply hangs or dies
            // with no status, and nothing says the target was unreachable.
            proxy.on('error', (error, request) => {
              console.error(`[gridcore] proxy error for ${request.url} → ${target}: ${error.message}`);
            });
            proxy.on('proxyRes', (response, request) => {
              if ((response.statusCode ?? 0) >= 400) {
                console.warn(`[gridcore] ${request.method} ${request.url} → ${response.statusCode}`);
              }
            });
          },
        },
      },
    },
    define: {
      __DEV_MODE__: JSON.stringify(mode),
    },
    test: {
      environment: 'jsdom',
      globals: true,
      setupFiles: ['./src/test/setup.ts'],
      css: false,
      include: ['src/**/*.test.{ts,tsx}'],
      // Fast tier (CONVENTIONS.md ⚡): no browser, no server, parallel by default.
      restoreMocks: true,
    },
  };
});
