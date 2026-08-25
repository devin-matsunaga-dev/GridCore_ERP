import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '@/test/render';
import { DataTable, rowButtonToMove, type Column } from './data-table';

type Row = { id: string; name: string; onHand: number };

const rows: Row[] = [
  { id: '1', name: 'LV connector', onHand: 120 },
  { id: '2', name: 'Pole cross-arm', onHand: 8 },
];

const columns: Column<Row>[] = [
  { key: 'name', header: 'Item', wide: true, sortValue: (row) => row.name, cell: (row) => row.name },
  {
    key: 'onHand',
    header: 'On hand',
    align: 'right',
    sortValue: (row) => row.onHand,
    cell: (row) => row.onHand,
  },
  { key: 'note', header: 'Note', cell: () => 'n/a' },
];

function renderTable(props: Partial<Parameters<typeof DataTable<Row>>[0]> = {}) {
  return renderWithProviders(
    <DataTable
      label="Stock items"
      columns={columns}
      rows={rows}
      rowKey={(row) => row.id}
      {...props}
    />,
  );
}

describe('DataTable', () => {
  it('renders a header and a row per record', () => {
    renderTable();

    expect(screen.getByRole('table', { name: 'Stock items' })).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: /item/i })).toBeInTheDocument();
    expect(screen.getByText('LV connector')).toBeInTheDocument();
    expect(screen.getByText('Pole cross-arm')).toBeInTheDocument();
  });

  it('offers a sort control only on columns that declare one', async () => {
    const onSortChange = vi.fn();
    renderTable({ onSortChange });

    await userEvent.click(screen.getByRole('button', { name: /item/i }));
    expect(onSortChange).toHaveBeenCalledWith('name');

    // The unsortable column is a plain header — no control to press.
    const note = screen.getByRole('columnheader', { name: 'Note' });
    expect(within(note).queryByRole('button')).not.toBeInTheDocument();
  });

  it('announces the sorted column and its direction', () => {
    renderTable({ onSortChange: vi.fn(), sort: { key: 'onHand', direction: 'desc' } });

    expect(screen.getByRole('columnheader', { name: /on hand/i })).toHaveAttribute(
      'aria-sort',
      'descending',
    );
    expect(screen.getByRole('columnheader', { name: /item/i })).toHaveAttribute('aria-sort', 'none');
  });

  /** The quality floor: a detail a mouse can open, a keyboard must be able to open too. */
  it('activates a row by click and by keyboard', async () => {
    const onRowActivate = vi.fn();
    renderTable({ onRowActivate });

    const [first] = screen.getAllByRole('button', { name: /LV connector/ });
    await userEvent.click(first!);
    expect(onRowActivate).toHaveBeenCalledWith(rows[0]);

    onRowActivate.mockClear();
    first!.focus();
    await userEvent.keyboard('{Enter}');
    expect(onRowActivate).toHaveBeenCalledWith(rows[0]);

    onRowActivate.mockClear();
    await userEvent.keyboard(' ');
    expect(onRowActivate).toHaveBeenCalledWith(rows[0]);
  });

  it('leaves rows inert when nothing opens', () => {
    renderTable();

    expect(screen.queryByRole('button', { name: /LV connector/ })).not.toBeInTheDocument();
  });

  /** The marker sits on the row, which keeps its row role — the button is inside one cell. */
  it('marks the row whose detail is open', () => {
    renderTable({ onRowActivate: vi.fn(), isRowActive: (row) => row.id === '2' });

    const [, first, second] = screen.getAllByRole('row');
    expect(second).toHaveAttribute('aria-current', 'true');
    expect(first).not.toHaveAttribute('aria-current');
  });

  /**
   * A `role="button"` on the `<tr>` would take the row out of the table for a screen reader, so the
   * control lives in the primary cell and the row stays a row.
   */
  it('keeps every row a table row, with one control in the primary cell', () => {
    renderTable({ onRowActivate: vi.fn() });

    // Header row plus one per record, all still rows.
    expect(screen.getAllByRole('row')).toHaveLength(3);

    const [, firstRow] = screen.getAllByRole('row');
    expect(within(firstRow!).getAllByRole('button')).toHaveLength(1);
    expect(within(firstRow!).getByRole('button')).toHaveAccessibleName('LV connector');
  });

  /** Failure path: no rows and nothing loading is an empty state, not a bare header. */
  it('renders the empty state instead of an empty table', () => {
    renderTable({ rows: [], empty: <p>Nothing in the catalogue yet</p> });

    expect(screen.getByText('Nothing in the catalogue yet')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  /** DESIGN.md: skeleton shimmer, never a spinner — and never the empty state while loading. */
  it('shows skeleton rows while loading, not the empty state', () => {
    const { container } = renderTable({
      rows: [],
      isLoading: true,
      empty: <p>Nothing in the catalogue yet</p>,
    });

    expect(screen.queryByText('Nothing in the catalogue yet')).not.toBeInTheDocument();
    expect(container.querySelectorAll('[data-slot="skeleton"]').length).toBeGreaterThan(0);
  });
});

/**
 * Arrow-key row navigation (WP-2.9). On top of Tab, never instead of it: a screen-reader user
 * crossing the table cell by cell is unaffected, and a rep who typed in the filter above can reach
 * the answer with two keys.
 */
describe('rowButtonToMove', () => {
  const buttons = ['a', 'b', 'c'].map((name) => ({ name }) as unknown as HTMLElement);

  it('enters the list from outside it', () => {
    // Down from the filter box above the table selects the first row, which is what makes
    // type-Down-Enter reach the best match without the mouse.
    expect(rowButtonToMove(buttons, null, 'ArrowDown')).toBe(buttons[0]);
  });

  it('does not enter the list on the way up', () => {
    // Up out of a filter box belongs to whatever is above it, not to the table.
    expect(rowButtonToMove(buttons, null, 'ArrowUp')).toBeUndefined();
  });

  it('walks the rows', () => {
    expect(rowButtonToMove(buttons, buttons[0]!, 'ArrowDown')).toBe(buttons[1]);
    expect(rowButtonToMove(buttons, buttons[1]!, 'ArrowUp')).toBe(buttons[0]);
  });

  it('clamps rather than wrapping', () => {
    // A table is one page of a longer register, so wrapping would leave both arrows going somewhere
    // unexpected.
    expect(rowButtonToMove(buttons, buttons[2]!, 'ArrowDown')).toBe(buttons[2]);
    expect(rowButtonToMove(buttons, buttons[0]!, 'ArrowUp')).toBeUndefined();
  });

  it('jumps to the ends, but only from inside the list', () => {
    expect(rowButtonToMove(buttons, buttons[1]!, 'Home')).toBe(buttons[0]);
    expect(rowButtonToMove(buttons, buttons[1]!, 'End')).toBe(buttons[2]);
    expect(rowButtonToMove(buttons, null, 'End')).toBeUndefined();
  });

  it('leaves every other key alone', () => {
    // Typing must still reach the box the caret is in.
    expect(rowButtonToMove(buttons, buttons[0]!, 'a')).toBeUndefined();
    expect(rowButtonToMove(buttons, buttons[0]!, 'Enter')).toBeUndefined();
  });

  it('has nowhere to go in an empty table', () => {
    expect(rowButtonToMove([], null, 'ArrowDown')).toBeUndefined();
  });
});

describe('DataTable keyboard navigation', () => {
  it('moves focus between rows with the arrow keys and activates with Enter', async () => {
    const onRowActivate = vi.fn();

    renderTable({ onRowActivate });

    const [first, second] = screen.getAllByRole('button', { name: /LV connector|Pole cross-arm/ });

    first!.focus();
    await userEvent.keyboard('{ArrowDown}');
    expect(second).toHaveFocus();

    await userEvent.keyboard('{ArrowUp}');
    expect(first).toHaveFocus();

    // Enter is the button's own — nothing had to reimplement activation to add the arrows.
    await userEvent.keyboard('{Enter}');
    expect(onRowActivate).toHaveBeenCalledWith(rows[0]);
  });

  it('leaves the arrows alone when there is no detail to open', async () => {
    // A read-only table has no row buttons, so the keys belong to the page's own scrolling.
    renderTable();

    expect(screen.queryByRole('button', { name: /LV connector/ })).not.toBeInTheDocument();
  });
});
