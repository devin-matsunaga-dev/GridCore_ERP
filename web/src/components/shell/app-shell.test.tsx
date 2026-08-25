import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Route, Routes } from 'react-router';
import { renderWithProviders } from '@/test/render';
import { AppShell } from './app-shell';

/**
 * The frame's layout contract.
 *
 * jsdom does no layout — there are no boxes to measure and `scrollHeight` is always zero — so these
 * assert the classes that produce the behaviour rather than the behaviour itself. That is a weaker
 * test than it looks and it is deliberately narrow: it exists to stop one specific class being
 * dropped, and it says why below.
 */
function renderShell() {
  return renderWithProviders(
    <Routes>
      <Route element={<AppShell />}>
        <Route path="/billing" element={<p>Routed page</p>} />
      </Route>
    </Routes>,
    { route: '/billing' },
  );
}

describe('AppShell', () => {
  it('scrolls the content column rather than the document', () => {
    renderShell();

    const column = screen.getByText('Routed page').closest('div.overflow-y-auto');

    expect(column).not.toBeNull();
    // The app's own thin scrollbar, per DESIGN.md's quality floor — never the browser's.
    expect(column).toHaveClass('scrollbar-subtle');
    // Panes never scroll sideways.
    expect(column).toHaveClass('overflow-x-hidden');
  });

  /**
   * The regression. `.sr-only` is `position: absolute`, and an absolutely positioned box with no
   * positioned ancestor gets the *initial* containing block — so it is not clipped by the shell's
   * `overflow-hidden`, and it stretches the DOCUMENT's scroll height to its own unscrolled flow
   * position. On a page taller than the viewport that shows as a browser scrollbar and a band of
   * blank white below the shell, which is exactly what the revenue-cycle walk produced: every step
   * card carries an `.sr-only` label, and the last of them sits far below the fold.
   */
  it('positions the scrolling column so absolute descendants cannot escape it', () => {
    renderShell();

    const column = screen.getByText('Routed page').closest('div.overflow-y-auto');

    expect(column).toHaveClass('relative');
  });

  it('renders the routed page inside that column, not beside it', () => {
    renderShell();

    // A page mounted outside the scroller would scroll the document however the column is styled.
    const column = screen.getByText('Routed page').closest('div.overflow-y-auto');

    expect(column?.contains(screen.getByRole('main'))).toBe(true);
  });
});
