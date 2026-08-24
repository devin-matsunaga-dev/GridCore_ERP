import { screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { registryWindow } from '@/api/registry';
import { renderWithProviders } from '@/test/render';
import type { Column } from './data-table';
import { RegistryTableCard } from './registry-table-card';
import type { TableState } from './table-state';

type Row = { id: string; name: string };

const columns: Column<Row>[] = [{ key: 'name', header: 'Name', cell: (row) => row.name }];

function tableState(rows: Row[]): TableState<Row> {
  return {
    sort: null,
    toggleSort: vi.fn(),
    page: 1,
    setPage: vi.fn(),
    pageSize: 10,
    setPageSize: vi.fn(),
    pageRows: rows.slice(0, 10),
    totalRows: rows.length,
  };
}

function rowsOfLength(length: number): Row[] {
  return Array.from({ length }, (_, index) => ({ id: String(index), name: `Row ${index}` }));
}

function renderCard(props: Partial<Parameters<typeof RegistryTableCard<Row>>[0]> = {}) {
  const rows = rowsOfLength(3);

  return renderWithProviders(
    <RegistryTableCard
      label="Rows"
      columns={columns}
      table={tableState(rows)}
      rowKey={(row) => row.id}
      {...props}
    />,
  );
}

describe('RegistryTableCard', () => {
  it('shows the table and its pagination', () => {
    renderCard();

    expect(screen.getByRole('table', { name: 'Rows' })).toBeInTheDocument();
    expect(screen.getByLabelText('Rows per page')).toBeInTheDocument();
  });

  /**
   * The list endpoints report no total, so a full window may have had rows cut off the end. Sorting
   * and paging a slice is fine — passing it off as the whole registry is not.
   */
  it('warns when the answer filled the server window', () => {
    renderCard({ returnedRows: registryWindow, table: tableState(rowsOfLength(registryWindow)) });

    expect(screen.getByText(/Showing the first 200 matches/)).toBeInTheDocument();
  });

  it('stays quiet when the answer fitted inside the window', () => {
    renderCard({ returnedRows: registryWindow - 1 });

    expect(screen.queryByText(/Showing the first/)).not.toBeInTheDocument();
  });

  /** Failure path: a failed query replaces the table, and takes the pagination with it. */
  it('replaces the table with the error, and hides the pagination', () => {
    renderCard({ error: new Error('Boom'), onRetry: vi.fn() });

    expect(screen.getByRole('alert')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Rows per page')).not.toBeInTheDocument();
  });

  /** A full window plus a failure is a failure — the notice would be describing rows nobody got. */
  it('does not warn about truncation on a failed query', () => {
    renderCard({ error: new Error('Boom'), returnedRows: registryWindow });

    expect(screen.queryByText(/Showing the first/)).not.toBeInTheDocument();
  });

  it('hides the pagination while the first page is loading', () => {
    renderCard({ isLoading: true, table: tableState([]) });

    expect(screen.queryByLabelText('Rows per page')).not.toBeInTheDocument();
  });
});
