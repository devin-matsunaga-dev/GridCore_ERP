import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '@/test/render';
import { Topbar, firstNameOf, greetingFor } from './topbar';

describe('greetingFor', () => {
  it.each([
    [new Date(2026, 7, 24, 7), 'Good morning'],
    [new Date(2026, 7, 24, 13), 'Good afternoon'],
    [new Date(2026, 7, 24, 21), 'Good evening'],
  ])('greets at %s with %s', (now, expected) => {
    expect(greetingFor(now)).toBe(expected);
  });
});

describe('firstNameOf', () => {
  it('takes the first word of a display name', () => {
    expect(firstNameOf('Jordan Smith')).toBe('Jordan');
  });

  /** Failure path: a token carrying only an email must still greet the person by name. */
  it('falls back to the local part of an email', () => {
    expect(firstNameOf('jordan.smith@rota-utilities.test')).toBe('jordan');
  });
});

describe('Topbar', () => {
  it('greets the signed-in user and shows the organisation', () => {
    renderWithProviders(<Topbar onOpenNavigation={vi.fn()} />);

    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent(/, Jordan\.$/);
    expect(screen.getByText('Rota Utilities')).toBeInTheDocument();
  });

  it('shows the unread notification count', () => {
    renderWithProviders(<Topbar onOpenNavigation={vi.fn()} notificationCount={3} />);

    expect(screen.getByRole('button', { name: 'Notifications (3)' })).toHaveTextContent('3');
  });

  it('focuses the search box on the ⌘K / Ctrl-K shortcut', async () => {
    renderWithProviders(<Topbar onOpenNavigation={vi.fn()} />);
    const search = screen.getByRole('searchbox', { name: 'Search GridCore' });

    expect(search).not.toHaveFocus();
    await userEvent.keyboard('{Control>}k{/Control}');

    expect(search).toHaveFocus();
  });

  it('opens the navigation drawer from the small-screen menu button', async () => {
    const onOpenNavigation = vi.fn();
    renderWithProviders(<Topbar onOpenNavigation={onOpenNavigation} />);

    await userEvent.click(screen.getByRole('button', { name: 'Open navigation' }));

    expect(onOpenNavigation).toHaveBeenCalledOnce();
  });
});
