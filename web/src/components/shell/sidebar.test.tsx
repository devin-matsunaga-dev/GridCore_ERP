import { screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '@/test/render';
import { navigationItems } from './navigation';
import { Sidebar } from './sidebar';

vi.mock('react-oidc-context', () => ({
  useAuth: () => ({ signoutRedirect: vi.fn() }),
}));

describe('Sidebar', () => {
  it('renders every nav item under its section heading', () => {
    renderWithProviders(<Sidebar />);

    const nav = screen.getByRole('navigation', { name: 'Main' });

    for (const section of ['Operations', 'Enterprise', 'Reports']) {
      expect(within(nav).getByRole('heading', { name: section })).toBeInTheDocument();
    }

    for (const item of navigationItems) {
      expect(within(nav).getByRole('link', { name: item.label })).toHaveAttribute('href', item.to);
    }
  });

  it('marks only the current route as active', () => {
    renderWithProviders(<Sidebar />, { route: '/assets' });

    expect(screen.getByRole('link', { name: 'Assets' })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('link', { name: 'Home' })).not.toHaveAttribute('aria-current');
  });

  /** `/` would otherwise prefix-match every route and light Home up everywhere. */
  it('does not mark Home active on a child route', () => {
    renderWithProviders(<Sidebar />, { route: '/finance' });

    expect(screen.getByRole('link', { name: 'Home' })).not.toHaveAttribute('aria-current');
    expect(screen.getByRole('link', { name: 'Finance' })).toHaveAttribute('aria-current', 'page');
  });

  it('shows the signed-in user and their role in the pinned user card', () => {
    renderWithProviders(<Sidebar />);

    expect(screen.getByText('Jordan Smith')).toBeInTheDocument();
    expect(screen.getByText('Operations Supervisor')).toBeInTheDocument();
    expect(screen.getByText('JS')).toBeInTheDocument();
  });

  /** Failure path: /api/me has not answered yet — a skeleton, never a spinner or a blank card. */
  it('shows a skeleton while the current user is still loading', () => {
    renderWithProviders(<Sidebar />, { currentUser: null });

    expect(screen.queryByText('Jordan Smith')).not.toBeInTheDocument();
    expect(document.querySelectorAll('[data-slot="skeleton"]').length).toBeGreaterThan(0);
  });
});
