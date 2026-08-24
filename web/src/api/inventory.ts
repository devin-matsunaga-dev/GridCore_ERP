import { useQuery } from '@tanstack/react-query';
import { api } from './client';
import { registryWindow } from './registry';

/** The Inventory module's client — the stocked catalogue, per-warehouse levels and the stock ledger. */

/** Mirrors `GridCore.Modules.Inventory.Features.Items.StockItemCategory`. */
export const stockItemCategories = [
  'Conductor',
  'Hardware',
  'Transformer',
  'Metering',
  'Consumable',
  'Tooling',
  'Safety',
] as const;
export type StockItemCategory = (typeof stockItemCategories)[number];

/** Mirrors `UnitOfMeasure`. */
export type UnitOfMeasure = 'Each' | 'Metre' | 'Kilogram' | 'Litre';

/** How a unit reads beside a quantity. `Each` is a count, so it renders bare. */
const unitSuffixes: Record<UnitOfMeasure, string> = {
  Each: '',
  Metre: 'm',
  Kilogram: 'kg',
  Litre: 'L',
};

export function unitSuffix(unit: UnitOfMeasure): string {
  return unitSuffixes[unit] ?? '';
}

/** Mirrors `StockMovementType`. */
export const stockMovementTypes = ['Receipt', 'Issue', 'Adjustment'] as const;
export type StockMovementType = (typeof stockMovementTypes)[number];

/** Mirrors `WarehouseResponse`. Reference data — read-only, shipped by migration. */
export type Warehouse = {
  id: string;
  code: string;
  name: string;
  location: string | null;
  isActive: boolean;
  /** Catalogue lines this store holds, so a summary needs no second request. */
  linesHeld: number;
  linesBelowMinimum: number;
};

/** Mirrors `StockLevelResponse` — one item in one warehouse. */
export type StockLevel = {
  warehouseId: string;
  quantityOnHand: number;
  /** Zero means nobody has set a reorder level, which is not the same as "low". */
  minimumQuantity: number;
  isBelowMinimum: boolean;
  lastMovedAt: string | null;
};

/** Mirrors `StockMovementResponse` — one append-only ledger line. */
export type StockMovement = {
  id: string;
  movementType: StockMovementType;
  warehouseId: string;
  /** Signed: a receipt is positive, an issue negative, an adjustment either way. */
  quantityChange: number;
  quantityOnHandAfter: number;
  unitCost: number | null;
  value: number | null;
  reference: string | null;
  workOrderId: string | null;
  note: string | null;
  actorId: string;
  actorName: string | null;
  recordedAt: string;
};

/** Mirrors `StockItemResponse`. */
export type StockItem = {
  id: string;
  itemCode: string;
  name: string;
  category: StockItemCategory;
  unit: UnitOfMeasure;
  description: string | null;
  manufacturerPartNumber: string | null;
  /** Standard cost, not a moving average — a receipt records its own cost on its ledger line. */
  unitCost: number;
  isActive: boolean;
  statusReason: string | null;
  totalOnHand: number;
  isBelowMinimum: boolean;
  registeredAt: string;
  levels: StockLevel[];
  /** Empty on a list response by design; the detail read is what carries the ledger. */
  movements: StockMovement[];
};

export type StockItemFilters = {
  search?: string;
  category?: StockItemCategory | '';
  warehouseId?: string;
  belowMinimum?: boolean;
  includeInactive?: boolean;
};

export type StockMovementFilters = {
  warehouseId?: string;
  movementType?: StockMovementType | '';
};

function params(
  filters: Record<string, string | boolean | undefined>,
): Record<string, string | boolean> {
  return Object.fromEntries(
    Object.entries(filters).filter(([, value]) => value !== undefined && value !== '' && value !== false),
  ) as Record<string, string | boolean>;
}

export const inventoryApi = {
  listWarehouses: (signal?: AbortSignal) =>
    api.get<Warehouse[]>('/api/inventory/warehouses', { signal }),
  listItems: (filters: StockItemFilters, signal?: AbortSignal) =>
    api.get<StockItem[]>('/api/inventory/items', {
      query: { ...params({ ...filters }), limit: registryWindow },
      signal,
    }),
  getItem: (id: string, signal?: AbortSignal) =>
    api.get<StockItem>(`/api/inventory/items/${id}`, { signal }),
  movements: (id: string, filters: StockMovementFilters, signal?: AbortSignal) =>
    api.get<StockMovement[]>(`/api/inventory/items/${id}/movements`, {
      query: params({ ...filters }),
      signal,
    }),
};

export const inventoryKeys = {
  all: ['inventory'] as const,
  warehouses: () => ['inventory', 'warehouses'] as const,
  items: (filters: StockItemFilters) => ['inventory', 'items', filters] as const,
  item: (id: string) => ['inventory', 'item', id] as const,
  movements: (id: string, filters: StockMovementFilters) =>
    ['inventory', 'movements', id, filters] as const,
};

export function useWarehouses() {
  return useQuery({
    queryKey: inventoryKeys.warehouses(),
    queryFn: ({ signal }) => inventoryApi.listWarehouses(signal),
    // Reference data: it only changes when a migration ships.
    staleTime: 5 * 60 * 1000,
  });
}

export function useStockItems(filters: StockItemFilters) {
  return useQuery({
    queryKey: inventoryKeys.items(filters),
    queryFn: ({ signal }) => inventoryApi.listItems(filters, signal),
  });
}

export function useStockItem(id: string | undefined) {
  return useQuery({
    queryKey: inventoryKeys.item(id ?? ''),
    queryFn: ({ signal }) => inventoryApi.getItem(id!, signal),
    enabled: Boolean(id),
  });
}

export function useStockMovements(id: string | undefined, filters: StockMovementFilters) {
  return useQuery({
    queryKey: inventoryKeys.movements(id ?? '', filters),
    queryFn: ({ signal }) => inventoryApi.movements(id!, filters, signal),
    enabled: Boolean(id),
  });
}

/** Warehouse names by id, for the level and ledger tables that carry only the id. */
export function warehouseNames(warehouses: Warehouse[] | undefined): Map<string, string> {
  return new Map((warehouses ?? []).map((warehouse) => [warehouse.id, warehouse.name]));
}
