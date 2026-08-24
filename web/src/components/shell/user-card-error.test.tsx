import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '@/test/render';
import { UserCard } from './user-card';

vi.mock('react-oidc-context', () => ({
  useAuth: () => ({ signoutRedirect: vi.fn() }),
}));

/**
 * `/api/me` failing is the case that actually broke in the browser, so it gets its own file: the
 * query has to really run and really reject, which means no seeded cache.
 */
describe('UserCard when /api/me fails', () => {
  beforeEach(() => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(new Response('{}', { status: 401, headers: { 'Content-Type': 'application/json' } })),
    );
  });

  afterEach(() => vi.unstubAllGlobals());

  it('says the details are unavailable and still offers sign out and a retry', async () => {
    renderWithProviders(<UserCard />, { currentUser: null });

    expect(await screen.findByText('Details unavailable')).toBeInTheDocument();
    expect(screen.getByText('Account')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Account menu' }));

    expect(await screen.findByRole('menuitem', { name: 'Sign out' })).toBeInTheDocument();
    expect(screen.getByRole('menuitem', { name: 'Retry loading account' })).toBeInTheDocument();
  });
});
