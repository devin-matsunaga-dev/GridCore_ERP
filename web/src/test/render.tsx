import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, type RenderOptions, type RenderResult } from '@testing-library/react';
import type { ReactElement, ReactNode } from 'react';
import { MemoryRouter } from 'react-router';
import { identityKeys, type CurrentUser } from '@/api/identity';
import { ThemeProvider } from '@/theme/theme-provider';

export const testUser: CurrentUser = {
  userId: 'ab5c1f6e-2f19-4a4b-9d54-6c9b6f6f2a11',
  userName: 'Jordan Smith',
  email: 'jordan.smith@rota-utilities.test',
  roles: ['Supervisor', 'Technician'],
  permissions: ['work-orders.read'],
};

export type RenderWithProvidersOptions = Omit<RenderOptions, 'wrapper'> & {
  route?: string;
  /** Seeds `/api/me` in the cache. `null` leaves it unresolved, so components render their loading state. */
  currentUser?: CurrentUser | null;
};

/**
 * Renders inside the providers the shell needs, with the network stubbed out — the fast tier
 * (CONVENTIONS.md ⚡) never reaches a server.
 */
export function renderWithProviders(
  ui: ReactElement,
  { route = '/', currentUser = testUser, ...options }: RenderWithProvidersOptions = {},
): RenderResult & { queryClient: QueryClient } {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });

  if (currentUser) {
    queryClient.setQueryData(identityKeys.me(), currentUser);
  }

  function Wrapper({ children }: { children: ReactNode }) {
    return (
      <ThemeProvider>
        <QueryClientProvider client={queryClient}>
          <MemoryRouter initialEntries={[route]}>{children}</MemoryRouter>
        </QueryClientProvider>
      </ThemeProvider>
    );
  }

  return { ...render(ui, { wrapper: Wrapper, ...options }), queryClient };
}
