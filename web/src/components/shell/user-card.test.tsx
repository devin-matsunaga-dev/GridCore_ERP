import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { renderWithProviders, testUser } from '@/test/render';
import { UserCard, initialsOf, primaryRoleTitle } from './user-card';

const signoutRedirect = vi.fn();

vi.mock('react-oidc-context', () => ({
  useAuth: () => ({ signoutRedirect }),
}));

describe('initialsOf', () => {
  it.each([
    ['Jordan Smith', 'JS'],
    ['jordan smith', 'JS'],
    ['Maria de la Cruz', 'MC'],
    ['Jordan', 'JO'],
  ])('renders %s as %s', (name, expected) => {
    expect(initialsOf(name)).toBe(expected);
  });

  /** Failure path: a token with no usable name must not crash the sidebar. */
  it('falls back for an empty name', () => {
    expect(initialsOf('   ')).toBe('?');
  });
});

describe('primaryRoleTitle', () => {
  it('picks the highest-ranking role held', () => {
    expect(primaryRoleTitle(['Technician', 'Supervisor'])).toBe('Operations Supervisor');
  });

  it('describes a caller holding no GridCore role', () => {
    expect(primaryRoleTitle([])).toBe('GridCore user');
  });

  /** Roles the realm carries for other systems must not become a job title. */
  it('ignores roles GridCore does not define', () => {
    expect(primaryRoleTitle(['offline_access', 'default-roles-gridcore'])).toBe('GridCore user');
  });
});

describe('UserCard', () => {
  it('shows the signed-in person and their role', () => {
    renderWithProviders(<UserCard />);

    expect(screen.getByText(testUser.userName!)).toBeInTheDocument();
    expect(screen.getByText('Operations Supervisor')).toBeInTheDocument();
  });

  it('opens the account menu and signs out', async () => {
    renderWithProviders(<UserCard />);

    await userEvent.click(screen.getByRole('button', { name: 'Account menu' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Sign out' }));

    expect(signoutRedirect).toHaveBeenCalledOnce();
  });

  /**
   * The bug this guards: the loading state used to replace the whole button with a skeleton, so a
   * slow or stalled `/api/me` left the card showing nothing and offering no way to sign out.
   */
  it('stays clickable while the account is still loading', async () => {
    renderWithProviders(<UserCard />, { currentUser: null });

    const trigger = screen.getByRole('button', { name: 'Account menu' });
    expect(trigger).toBeEnabled();
    expect(document.querySelectorAll('[data-slot="skeleton"]').length).toBeGreaterThan(0);

    await userEvent.click(trigger);
    expect(await screen.findByRole('menuitem', { name: 'Sign out' })).toBeInTheDocument();
  });
});
