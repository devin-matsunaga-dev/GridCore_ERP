import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { renderWithProviders } from '@/test/render';
import { workOrderFeed } from '../demo-data';
import { WorkOrderFeed, pageOf } from './work-order-feed';

describe('pageOf', () => {
  const rows = Array.from({ length: 20 }, (_, index) => index);

  it('slices the requested page', () => {
    expect(pageOf(rows, 1, 5)).toEqual([0, 1, 2, 3, 4]);
    expect(pageOf(rows, 3, 5)).toEqual([10, 11, 12, 13, 14]);
  });

  it('returns a short final page rather than padding it', () => {
    expect(pageOf(rows.slice(0, 12), 3, 5)).toEqual([10, 11]);
  });

  /** Failure path: a page past the end would otherwise render an empty table. */
  it('clamps a page number outside the range', () => {
    expect(pageOf(rows, 99, 5)).toEqual([15, 16, 17, 18, 19]);
    expect(pageOf(rows, 0, 5)).toEqual([0, 1, 2, 3, 4]);
  });
});

describe('WorkOrderFeed', () => {
  it('pages through the rows', async () => {
    renderWithProviders(<WorkOrderFeed rows={workOrderFeed} />);

    const table = screen.getByRole('table');
    expect(within(table).getByText(workOrderFeed[0]!.id)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Page 2' }));

    expect(within(table).queryByText(workOrderFeed[0]!.id)).not.toBeInTheDocument();
    expect(within(table).getByText(workOrderFeed[5]!.id)).toBeInTheDocument();
    expect(screen.getByText('6–10 of 20')).toBeInTheDocument();
  });

  /**
   * Failure path: page 4 does not exist at 10 rows a page. Returning to page 1 is what stops the
   * table rendering empty after the size changes.
   */
  it('returns to the first page when the page size changes', async () => {
    renderWithProviders(<WorkOrderFeed rows={workOrderFeed} />);

    await userEvent.click(screen.getByRole('button', { name: 'Page 4' }));
    expect(screen.getByText('16–20 of 20')).toBeInTheDocument();

    await userEvent.selectOptions(screen.getByLabelText('Rows per page'), '10');

    expect(screen.getByText('1–10 of 20')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Page 1' })).toHaveAttribute('aria-current', 'page');
  });

  it('offers the filter and overflow affordances from the reference design', () => {
    renderWithProviders(<WorkOrderFeed rows={workOrderFeed} />);

    expect(screen.getByRole('button', { name: 'Filters' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Work order feed options' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'View all' })).toHaveAttribute('href', '/work-orders');
  });
});
