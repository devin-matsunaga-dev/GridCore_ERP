import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '@/test/render';
import { RequireAuth } from './require-auth';

const signinRedirect = vi.fn();
const authState = {
  isAuthenticated: false,
  isLoading: false,
  activeNavigator: undefined as string | undefined,
  error: undefined as Error | undefined,
  signinRedirect,
};

vi.mock('react-oidc-context', () => ({
  useAuth: () => authState,
}));

function Protected() {
  return <p>Grid operations console</p>;
}

describe('RequireAuth', () => {
  beforeEach(() => {
    signinRedirect.mockReset();
    Object.assign(authState, {
      isAuthenticated: false,
      isLoading: false,
      activeNavigator: undefined,
      error: undefined,
    });
  });

  /** Failure path: an unauthenticated visitor must never see the shell. */
  it('sends an unauthenticated visitor to the identity provider and renders nothing protected', () => {
    renderWithProviders(
      <RequireAuth>
        <Protected />
      </RequireAuth>,
    );

    expect(signinRedirect).toHaveBeenCalledOnce();
    expect(screen.queryByText('Grid operations console')).not.toBeInTheDocument();
    expect(screen.getByText('Signing you in')).toBeInTheDocument();
  });

  it('does not redirect while the provider is still restoring a session', () => {
    authState.isLoading = true;

    renderWithProviders(
      <RequireAuth>
        <Protected />
      </RequireAuth>,
    );

    expect(signinRedirect).not.toHaveBeenCalled();
  });

  /** Failure path: a failed sign-in must stop, not loop the redirect. */
  it('shows the error with a retry instead of redirecting again', async () => {
    authState.error = new Error('Realm gridcore is unreachable.');

    renderWithProviders(
      <RequireAuth>
        <Protected />
      </RequireAuth>,
    );

    expect(signinRedirect).not.toHaveBeenCalled();
    expect(screen.getByText('Sign-in failed')).toBeInTheDocument();
    expect(screen.getByText('Realm gridcore is unreachable.')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));

    expect(signinRedirect).toHaveBeenCalledOnce();
  });

  it('renders the protected content once the caller is authenticated', () => {
    authState.isAuthenticated = true;

    renderWithProviders(
      <RequireAuth>
        <Protected />
      </RequireAuth>,
    );

    expect(screen.getByText('Grid operations console')).toBeInTheDocument();
    expect(signinRedirect).not.toHaveBeenCalled();
  });
});
