import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '@/test/render';
import { Pagination, pageCount, rangeLabel } from './pagination';

describe('pageCount', () => {
  it('rounds a partial last page up', () => {
    expect(pageCount(20, 5)).toBe(4);
    expect(pageCount(21, 5)).toBe(5);
  });

  /** Failure path: an empty table still renders as page 1 of 1, never page 1 of 0. */
  it('never reports fewer than one page', () => {
    expect(pageCount(0, 5)).toBe(1);
  });
});

describe('rangeLabel', () => {
  it('describes the rows on the current page', () => {
    expect(rangeLabel(1, 5, 20)).toBe('1–5 of 20');
    expect(rangeLabel(3, 5, 20)).toBe('11–15 of 20');
  });

  /** The last page is short — the label must not overstate the row count. */
  it('clamps the final page to the real total', () => {
    expect(rangeLabel(3, 5, 12)).toBe('11–12 of 12');
  });

  it('describes an empty table', () => {
    expect(rangeLabel(1, 5, 0)).toBe('0 of 0');
  });
});

describe('Pagination', () => {
  it('marks the current page and moves on click', async () => {
    const onPageChange = vi.fn();
    renderWithProviders(
      <Pagination page={1} pageSize={5} totalRows={20} onPageChange={onPageChange} />,
    );

    expect(screen.getByRole('button', { name: 'Page 1' })).toHaveAttribute('aria-current', 'page');

    await userEvent.click(screen.getByRole('button', { name: 'Page 3' }));
    expect(onPageChange).toHaveBeenCalledWith(3);

    await userEvent.click(screen.getByRole('button', { name: 'Next page' }));
    expect(onPageChange).toHaveBeenCalledWith(2);
  });

  /** Failure path: the arrows must not walk off either end of the list. */
  it('disables the arrow that would leave the range', () => {
    const { rerender } = renderWithProviders(
      <Pagination page={1} pageSize={5} totalRows={20} onPageChange={vi.fn()} />,
    );

    expect(screen.getByRole('button', { name: 'Previous page' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Next page' })).toBeEnabled();

    rerender(<Pagination page={4} pageSize={5} totalRows={20} onPageChange={vi.fn()} />);

    expect(screen.getByRole('button', { name: 'Previous page' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Next page' })).toBeDisabled();
  });

  it('offers the rows-per-page control only when the caller handles it', () => {
    const { rerender } = renderWithProviders(
      <Pagination page={1} pageSize={5} totalRows={20} onPageChange={vi.fn()} />,
    );

    expect(screen.queryByLabelText('Rows per page')).not.toBeInTheDocument();

    rerender(
      <Pagination
        page={1}
        pageSize={5}
        totalRows={20}
        onPageChange={vi.fn()}
        onPageSizeChange={vi.fn()}
      />,
    );

    expect(screen.getByLabelText('Rows per page')).toBeInTheDocument();
  });
});
