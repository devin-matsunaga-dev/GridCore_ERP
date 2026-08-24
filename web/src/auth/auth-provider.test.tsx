import { render } from '@testing-library/react';
import { useEffect } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';

const useAuth = vi.fn();
vi.mock('react-oidc-context', () => ({
  useAuth: () => useAuth(),
  AuthProvider: ({ children }: { children: React.ReactNode }) => children,
}));

const { AuthProvider } = await import('./auth-provider');
const { api, clearAccessTokenProvider } = await import('@/api/client');

afterEach(() => {
  clearAccessTokenProvider();
  vi.unstubAllGlobals();
});

/**
 * The bug this guards: the token used to be published from an effect. A child's mount effect runs
 * *before* its parent's, so the shell's first `/api/me` went out with no Authorization header and
 * came back 401 — which the query client will not retry, leaving the sidebar with no account.
 */
describe('AuthProvider', () => {
  it('publishes the access token before a child can make its first request', async () => {
    useAuth.mockReturnValue({ user: { access_token: 'token-from-keycloak' } });

    const fetchMock = vi.fn().mockResolvedValue(new Response('{}', { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);

    function ChildThatFetchesOnMount() {
      useEffect(() => {
        void api.get('/api/me').catch(() => undefined);
      }, []);
      return null;
    }

    render(
      <AuthProvider>
        <ChildThatFetchesOnMount />
      </AuthProvider>,
    );

    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalled());

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect((init.headers as Record<string, string>).Authorization).toBe('Bearer token-from-keycloak');
  });

  /** Failure path: before sign-in there is no token, and requests must not send a bogus header. */
  it('sends no Authorization header while there is no signed-in user', async () => {
    useAuth.mockReturnValue({ user: undefined });

    const fetchMock = vi.fn().mockResolvedValue(new Response('{}', { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);

    render(
      <AuthProvider>
        <span />
      </AuthProvider>,
    );

    await api.get('/api/me');

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(init.headers).not.toHaveProperty('Authorization');
  });
});
