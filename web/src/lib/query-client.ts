import { MutationCache, QueryCache, QueryClient } from '@tanstack/react-query';
import { ApiError } from '@/api/client';
import { toast } from '@/components/feedback/toast';

/**
 * One place for server-state policy. A failed query surfaces through the shared toast, and an
 * expired session or a permission refusal is never retried — retrying a 403 just repeats it.
 */
export function createQueryClient(): QueryClient {
  return new QueryClient({
    queryCache: new QueryCache({
      onError: (error, query) => {
        // Background refetches of already-rendered data stay quiet; a first load does not.
        if (query.state.data === undefined) toast.apiError(error, 'Could not load that data.');
      },
    }),
    mutationCache: new MutationCache({
      onError: (error) => toast.apiError(error, 'Could not save that change.'),
    }),
    defaultOptions: {
      queries: {
        staleTime: 30_000,
        refetchOnWindowFocus: false,
        retry: (failureCount, error) => !isTerminal(error) && failureCount < 2,
      },
      mutations: { retry: false },
    },
  });
}

/** 4xx answers that will not change on a retry. */
function isTerminal(error: unknown): boolean {
  return error instanceof ApiError && error.status >= 400 && error.status < 500;
}
