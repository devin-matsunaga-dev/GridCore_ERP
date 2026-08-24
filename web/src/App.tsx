import { QueryClientProvider } from '@tanstack/react-query';
import { useState } from 'react';
import { BrowserRouter, Route, Routes } from 'react-router';
import { AuthProvider } from '@/auth/auth-provider';
import { RequireAuth } from '@/auth/require-auth';
import { Toaster } from '@/components/feedback/toaster';
import { AppShell } from '@/components/shell/app-shell';
import { DashboardPage } from '@/features/dashboard/dashboard-page';
import { createQueryClient } from '@/lib/query-client';
import { NotFoundPage } from '@/routes/not-found-page';
import { moduleRoutes } from '@/routes/routes';
import { ThemeProvider } from '@/theme/theme-provider';

export function App() {
  // Created once per app instance, not per render — a new client would drop the cache.
  const [queryClient] = useState(createQueryClient);

  return (
    <ThemeProvider>
      <QueryClientProvider client={queryClient}>
        <AuthProvider>
          <BrowserRouter>
            <RequireAuth>
              <Routes>
                <Route element={<AppShell />}>
                  <Route index element={<DashboardPage />} />
                  {moduleRoutes.map((route) => (
                    <Route key={route.path} path={route.path} element={route.element} />
                  ))}
                  <Route path="*" element={<NotFoundPage />} />
                </Route>
              </Routes>
            </RequireAuth>
          </BrowserRouter>
        </AuthProvider>
        <Toaster />
      </QueryClientProvider>
    </ThemeProvider>
  );
}
