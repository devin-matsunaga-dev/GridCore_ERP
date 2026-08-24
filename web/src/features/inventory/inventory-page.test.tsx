import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { InventoryPage } from './inventory-page';
import { stubFetch, type FetchStub, type StubbedResponse } from '@/test/api-stub';
import { stockItem, stockMovement, warehouse } from '@/test/registry-fixtures';
import { renderWithProviders } from '@/test/render';

const rota = warehouse();
const lowerBase = warehouse({
  id: '0192f000-0000-7000-8000-000000000602',
  code: 'LB',
  name: 'Lower Base Warehouse',
  location: 'Saipan',
  linesHeld: 11,
  linesBelowMinimum: 0,
});

const connector = stockItem();
const conductor = stockItem({
  id: '0192f000-0000-7000-8000-000000000702',
  itemCode: 'ITM-000002',
  name: 'ABC 3x35mm conductor',
  category: 'Conductor',
  unit: 'Metre',
  manufacturerPartNumber: null,
  unitCost: 3.2,
  totalOnHand: 240.5,
  isBelowMinimum: true,
  levels: [
    {
      warehouseId: rota.id,
      quantityOnHand: 240.5,
      minimumQuantity: 500,
      isBelowMinimum: true,
      lastMovedAt: '2026-06-02T00:30:00+00:00',
    },
  ],
});

let stub: FetchStub;

afterEach(() => stub?.restore());

function world(url: URL): StubbedResponse | undefined {
  if (url.pathname === '/api/inventory/warehouses') return { body: [lowerBase, rota] };
  if (url.pathname === '/api/inventory/items') return { body: [connector, conductor] };
  if (url.pathname === `/api/inventory/items/${conductor.id}`) return { body: conductor };
  if (url.pathname === `/api/inventory/items/${conductor.id}/movements`) {
    return {
      body: [
        stockMovement({ movementType: 'Issue', quantityChange: -59.5, quantityOnHandAfter: 240.5, reference: 'WO-114' }),
        stockMovement({ quantityChange: 300, quantityOnHandAfter: 300 }),
      ],
    };
  }
  return undefined;
}

function renderPage(respond: (url: URL) => StubbedResponse | undefined = world) {
  stub = stubFetch(respond);

  return renderWithProviders(<InventoryPage />, { route: '/inventory' });
}

describe('InventoryPage', () => {
  it('summarises the island stores without fetching the catalogue twice', async () => {
    renderPage();

    expect(await screen.findByText('Lower Base Warehouse')).toBeInTheDocument();
    expect(screen.getByText('Rota Warehouse')).toBeInTheDocument();
    // `linesHeld` and `linesBelowMinimum` ride on the warehouse response — WP-1.4 put them there
    // precisely so this summary is one request.
    expect(screen.getByText('11 lines held')).toBeInTheDocument();
    expect(screen.getByText('2 low')).toBeInTheDocument();
    expect(stub.calls.filter((url) => url.pathname === '/api/inventory/items')).toHaveLength(1);
  });

  it('lists the catalogue with quantities in the item’s own unit', async () => {
    renderPage();

    expect(await screen.findByText('LV service connector')).toBeInTheDocument();
    // Each is a count and renders bare; a metre carries its suffix.
    expect(screen.getByText('120')).toBeInTheDocument();
    expect(screen.getByText('240.5m')).toBeInTheDocument();
  });

  it('flags a line below its reorder level', async () => {
    renderPage();
    await screen.findByText('ABC 3x35mm conductor');

    const table = within(screen.getByRole('table', { name: 'Stock items' }));
    expect(table.getByText('Low stock')).toHaveClass('bg-danger-soft');
    expect(table.getByText('In stock')).toHaveClass('bg-success-soft');
  });

  /**
   * The two filters have to compose on the server: "low stock in the Rota store" means low *there*,
   * not "carried there and low anywhere" — WP-1.4's rule, which only holds if both reach the host.
   */
  it('composes the warehouse and low-stock filters into one request', async () => {
    renderPage();
    await screen.findByText('Rota Warehouse');

    await userEvent.click(screen.getByRole('button', { name: /Rota Warehouse/ }));
    await userEvent.click(screen.getByLabelText('Low stock only'));

    await waitFor(() => {
      const request = stub.lastCall('/api/inventory/items')!;
      expect(request.searchParams.get('warehouseId')).toBe(rota.id);
      expect(request.searchParams.get('belowMinimum')).toBe('true');
    });
  });

  it('clears the warehouse filter when the selected store is pressed again', async () => {
    renderPage();
    await screen.findByText('Rota Warehouse');

    const store = screen.getByRole('button', { name: /Rota Warehouse/ });
    await userEvent.click(store);
    await waitFor(() => expect(store).toHaveAttribute('aria-pressed', 'true'));

    await userEvent.click(store);
    await waitFor(() => {
      expect(stub.lastCall('/api/inventory/items')?.searchParams.has('warehouseId')).toBe(false);
    });
  });

  it('omits a false toggle rather than sending it', async () => {
    renderPage();
    await screen.findByText('LV service connector');

    const request = stub.lastCall('/api/inventory/items')!;
    expect(request.searchParams.has('belowMinimum')).toBe(false);
    expect(request.searchParams.has('includeInactive')).toBe(false);
  });

  /** The ledger is what explains the running total — the two are shown together on purpose. */
  it('opens the item drawer with its levels and its stock ledger', async () => {
    renderPage();
    await screen.findByText('ABC 3x35mm conductor');

    await userEvent.click(screen.getByRole('button', { name: /ABC 3x35mm conductor/ }));

    const drawer = await screen.findByRole('dialog', { name: 'ABC 3x35mm conductor' });
    expect(within(drawer).getByText('Reorder at 500m · last moved Jun 2, 2026')).toBeInTheDocument();

    const ledger = within(drawer).getByRole('table', { name: 'Stock movements' });
    // Signed changes, and the stamped running total the shelf must agree with.
    expect(within(ledger).getByText('-59.5m')).toBeInTheDocument();
    expect(within(ledger).getByText('+300m')).toBeInTheDocument();
  });

  it('narrows the ledger on the server', async () => {
    renderPage();
    await screen.findByText('ABC 3x35mm conductor');
    await userEvent.click(screen.getByRole('button', { name: /ABC 3x35mm conductor/ }));
    await screen.findByRole('dialog');

    await userEvent.selectOptions(screen.getByLabelText('Movement type'), 'Adjustment');

    await waitFor(() => {
      expect(
        stub.lastCall(`/api/inventory/items/${conductor.id}/movements`)?.searchParams.get('movementType'),
      ).toBe('Adjustment');
    });
  });

  /** Failure path: a permission refusal on the catalogue is a message, not an empty shelf. */
  it('reports a permission refusal on the catalogue', async () => {
    renderPage((url) => {
      if (url.pathname === '/api/inventory/warehouses') return { body: [rota] };
      if (url.pathname === '/api/inventory/items') {
        return { status: 403, body: { title: 'Forbidden', status: 403, detail: 'You do not hold inventory.read.' } };
      }
      return undefined;
    });

    expect(await screen.findByRole('alert')).toHaveTextContent('You do not have access to this');
  });
});
