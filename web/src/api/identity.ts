import { useQuery } from '@tanstack/react-query';
import { api } from './client';

/** Mirrors `GridCore.Platform.Security.MeResponse`. */
export type CurrentUser = {
  userId: string;
  userName: string | null;
  email: string | null;
  roles: string[];
  permissions: string[];
};

/** The Platform module's client. One typed client per module (CONVENTIONS.md). */
export const identityApi = {
  me: (signal?: AbortSignal) => api.get<CurrentUser>('/api/me', { signal }),
};

export const identityKeys = {
  me: () => ['identity', 'me'] as const,
};

/**
 * The signed-in caller as GridCore sees them — roles and permissions come from the API, never from
 * decoding the token in the browser, so what the UI shows matches what the API will allow.
 */
export function useCurrentUser() {
  return useQuery({
    queryKey: identityKeys.me(),
    queryFn: ({ signal }) => identityApi.me(signal),
    staleTime: 5 * 60 * 1000,
  });
}
