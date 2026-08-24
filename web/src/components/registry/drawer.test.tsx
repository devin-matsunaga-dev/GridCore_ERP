import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '@/test/render';
import { Drawer } from './drawer';

function renderDrawer(onClose = vi.fn(), open = true) {
  const result = renderWithProviders(
    <>
      <button type="button">Row that opened it</button>
      <Drawer open={open} onClose={onClose} title="Songsong pole-top transformer" subtitle={<span>AST-000001</span>}>
        <button type="button">First inside</button>
        <button type="button">Last inside</button>
      </Drawer>
    </>,
  );

  return { ...result, onClose };
}

describe('Drawer', () => {
  it('renders nothing when closed', () => {
    renderDrawer(vi.fn(), false);

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('is a modal dialog named by its title', () => {
    renderDrawer();

    const dialog = screen.getByRole('dialog', { name: 'Songsong pole-top transformer' });
    expect(dialog).toHaveAttribute('aria-modal', 'true');
    expect(screen.getByText('AST-000001')).toBeInTheDocument();
  });

  it('closes on Escape', async () => {
    const { onClose } = renderDrawer();

    await userEvent.keyboard('{Escape}');
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('closes on the overlay and on the close button', async () => {
    const { onClose } = renderDrawer();

    // Both are labelled the same way, so either one dismisses the panel.
    const [overlay, closeButton] = screen.getAllByRole('button', { name: 'Close details' });
    await userEvent.click(overlay!);
    await userEvent.click(closeButton!);

    expect(onClose).toHaveBeenCalledTimes(2);
  });

  it('moves focus into the panel when it opens', () => {
    renderDrawer();

    expect(screen.getByRole('dialog')).toHaveFocus();
  });

  /**
   * Failure path: without the wrap, Tab walks out of an open modal into the table behind it and a
   * keyboard user is reading a list they cannot see.
   */
  it('keeps Tab inside the panel', async () => {
    renderDrawer();

    const dialog = screen.getByRole('dialog');
    const closeButton = within(dialog).getByRole('button', { name: 'Close details' });

    await userEvent.tab();
    expect(closeButton).toHaveFocus();

    await userEvent.tab();
    expect(within(dialog).getByRole('button', { name: 'First inside' })).toHaveFocus();

    await userEvent.tab();
    expect(within(dialog).getByRole('button', { name: 'Last inside' })).toHaveFocus();

    // Past the last control: focus wraps to the first one rather than escaping to the page behind.
    await userEvent.tab();
    expect(closeButton).toHaveFocus();
    expect(screen.getByRole('button', { name: 'Row that opened it' })).not.toHaveFocus();
  });

  it('wraps backwards too', async () => {
    renderDrawer();

    const dialog = screen.getByRole('dialog');

    // Shift+Tab from the panel itself is the top of the loop, so it lands on the last control.
    await userEvent.tab({ shift: true });
    expect(within(dialog).getByRole('button', { name: 'Last inside' })).toHaveFocus();
  });

  /** Dismissing must put the caret back where it was, or a keyboard user restarts at the top. */
  it('restores focus to whatever opened it', async () => {
    const onClose = vi.fn();
    const { rerender } = renderWithProviders(
      <>
        <button type="button">Row that opened it</button>
        <Drawer open onClose={onClose} title="Detail">
          <button type="button">Inside</button>
        </Drawer>
      </>,
    );

    const opener = screen.getByRole('button', { name: 'Row that opened it' });
    opener.focus();

    rerender(
      <>
        <button type="button">Row that opened it</button>
        <Drawer open={false} onClose={onClose} title="Detail">
          <button type="button">Inside</button>
        </Drawer>
      </>,
    );

    expect(screen.getByRole('button', { name: 'Row that opened it' })).toHaveFocus();
  });

  it('locks the page behind it and releases the lock on close', () => {
    const { rerender } = renderWithProviders(
      <Drawer open onClose={vi.fn()} title="Detail">
        <p>Body</p>
      </Drawer>,
    );

    expect(document.body.style.overflow).toBe('hidden');

    rerender(
      <Drawer open={false} onClose={vi.fn()} title="Detail">
        <p>Body</p>
      </Drawer>,
    );

    expect(document.body.style.overflow).not.toBe('hidden');
  });
});
