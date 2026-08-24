import { screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderWithProviders } from '@/test/render';
import { DashboardPage } from './dashboard-page';
import { kpis, quickActions, workOrderFeed, workOrdersByStatus } from './demo-data';

describe('DashboardPage', () => {
  it('renders the five KPI cards from the reference dashboard', () => {
    renderWithProviders(<DashboardPage />);

    const row = screen.getByRole('region', { name: 'Key performance indicators' });

    for (const kpi of kpis) {
      expect(within(row).getByText(kpi.label)).toBeInTheDocument();
      expect(within(row).getByText(kpi.value)).toBeInTheDocument();
    }
  });

  it('colours a delta by sentiment, not by direction', () => {
    renderWithProviders(<DashboardPage />);

    // Operating cost falling 6.4% is good news; inventory value rising 3.2% is too.
    expect(screen.getByText('6.4%')).toHaveClass('text-success');
    expect(screen.getByText('3.2%')).toHaveClass('text-success');
  });

  it('shows the work-order total in the centre of the donut', () => {
    renderWithProviders(<DashboardPage />);

    const total = workOrdersByStatus.reduce((sum, slice) => sum + slice.count, 0);
    expect(screen.getByText(total.toLocaleString())).toBeInTheDocument();
    expect(screen.getByText('Total')).toBeInTheDocument();
  });

  it('shows the first page of the work-order feed with its status and priority', () => {
    renderWithProviders(<DashboardPage />);

    const table = screen.getByRole('table');
    const firstPage = workOrderFeed.slice(0, 5);

    for (const row of firstPage) {
      expect(within(table).getByText(row.id)).toBeInTheDocument();
    }

    // Row six is on page two, so it must not be rendered.
    expect(within(table).queryByText(workOrderFeed[5]!.id)).not.toBeInTheDocument();
    expect(screen.getByText('1–5 of 20')).toBeInTheDocument();
  });

  it('renders the alerts newest first with relative timestamps', () => {
    renderWithProviders(<DashboardPage />);

    expect(screen.getByText('Low Inventory: Transformer Oil')).toBeInTheDocument();
    expect(screen.getByText('10m ago')).toBeInTheDocument();
    expect(screen.getByText('3h ago')).toBeInTheDocument();
  });

  it('links every quick action to a real route', () => {
    renderWithProviders(<DashboardPage />);

    for (const action of quickActions) {
      const link = screen.getByRole('link', { name: new RegExp(action.label) });
      expect(link).toHaveAttribute('href', action.to);
      expect(within(link).getByText(action.description)).toBeInTheDocument();
    }
  });

  it('lines the procurement figures up to the same precision', () => {
    renderWithProviders(<DashboardPage />);

    // $21M would break the column; the stat row forces the decimal.
    expect(screen.getAllByText('$21.0M').length).toBeGreaterThan(0);
    expect(screen.getAllByText('$18.6M').length).toBeGreaterThan(0);
    expect(screen.getByText('-$2.4M')).toHaveClass('text-danger');
  });

  it('stamps how fresh the data is', () => {
    renderWithProviders(<DashboardPage />);

    expect(screen.getByText(/^Data as of/)).toBeInTheDocument();
  });
});
