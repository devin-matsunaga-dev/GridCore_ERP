import { Package } from 'lucide-react';
import { useState } from 'react';
import {
  stockItemCategories,
  unitSuffix,
  useStockItems,
  useWarehouses,
  type StockItem,
  type StockItemCategory,
} from '@/api/inventory';
import type { Column } from '@/components/registry/data-table';
import { orNotRecorded } from '@/components/registry/detail-list';
import { EmptyState } from '@/components/registry/empty-state';
import {
  ClearFilters,
  FilterBar,
  FilterSelect,
  FilterToggle,
  SearchField,
} from '@/components/registry/filter-bar';
import { PageHeader } from '@/components/registry/page-header';
import { RegistryTableCard } from '@/components/registry/registry-table-card';
import { useTableState } from '@/components/registry/table-state';
import { StatusPill } from '@/components/ui/status';
import { formatCount, formatLabel, formatMoney, formatQuantity } from '@/lib/format';
import { useDebouncedValue } from '@/lib/use-debounced-value';
import { StockItemDrawer } from './components/stock-item-drawer';
import { WarehouseSummary } from './components/warehouse-summary';

/**
 * The stocked catalogue, with the three island stores summarised above it.
 *
 * The warehouse and low-stock filters compose on the server: "low stock in the Rota store" means
 * low *there*, not "carried there and low anywhere" — which is WP-1.4's rule, and the reason both
 * go into the request rather than being applied to the rendered rows.
 */
export function InventoryPage() {
  const [search, setSearch] = useState('');
  const [category, setCategory] = useState<StockItemCategory | ''>('');
  const [warehouseId, setWarehouseId] = useState('');
  const [belowMinimum, setBelowMinimum] = useState(false);
  const [includeInactive, setIncludeInactive] = useState(false);
  const [openItemId, setOpenItemId] = useState<string | null>(null);

  const warehouses = useWarehouses();
  const query = useStockItems({
    search: useDebouncedValue(search),
    category,
    warehouseId,
    belowMinimum,
    includeInactive,
  });

  const table = useTableState({ rows: query.data, columns });

  const filtered =
    search !== '' || category !== '' || warehouseId !== '' || belowMinimum || includeInactive;

  return (
    <div className="space-y-6">
      <PageHeader
        title="Inventory"
        subtitle="What is on the shelf, in which store, and the ledger that explains it."
      />

      <WarehouseSummary
        warehouses={warehouses.data}
        isLoading={warehouses.isPending}
        selectedId={warehouseId}
        onSelect={setWarehouseId}
      />

      <RegistryTableCard
        label="Stock items"
        columns={columns}
        table={table}
        rowKey={(row) => row.id}
        isLoading={query.isPending}
        error={query.error}
        onRetry={() => void query.refetch()}
        returnedRows={query.data?.length}
        onRowActivate={(row) => setOpenItemId(row.id)}
        isRowActive={(row) => row.id === openItemId}
        filters={
          <FilterBar>
            <SearchField
              label="Search stock items"
              placeholder="Code, name, part number…"
              value={search}
              onChange={setSearch}
            />
            <FilterSelect
              label="Category"
              anyLabel="All categories"
              value={category}
              onChange={setCategory}
              options={stockItemCategories}
            />
            <FilterToggle label="Low stock only" checked={belowMinimum} onChange={setBelowMinimum} />
            <FilterToggle
              label="Include discontinued"
              checked={includeInactive}
              onChange={setIncludeInactive}
            />
            <ClearFilters
              show={filtered}
              onClear={() => {
                setSearch('');
                setCategory('');
                setWarehouseId('');
                setBelowMinimum(false);
                setIncludeInactive(false);
              }}
            />
            <p className="text-muted tabular ml-auto text-[13px]">
              {query.isPending ? '' : `${formatCount(table.totalRows)} shown`}
            </p>
          </FilterBar>
        }
        empty={
          <EmptyState
            icon={Package}
            title={filtered ? 'No items match those filters' : 'Nothing in the catalogue yet'}
            message={
              filtered
                ? 'Try a broader search, or clear the filters to see the whole catalogue.'
                : 'Stock items appear here once they are registered.'
            }
          />
        }
      />

      <StockItemDrawer
        itemId={openItemId}
        warehouses={warehouses.data}
        onClose={() => setOpenItemId(null)}
      />
    </div>
  );
}

const columns: Column<StockItem>[] = [
  {
    key: 'itemCode',
    header: 'Code',
    sortValue: (row) => row.itemCode,
    cell: (row) => <span className="text-muted tabular text-xs font-medium">{row.itemCode}</span>,
  },
  {
    key: 'name',
    header: 'Item',
    wide: true,
    primary: true,
    sortValue: (row) => row.name,
    cell: (row) => (
      <span className="block min-w-0">
        <span className="text-heading block truncate font-medium">{row.name}</span>
        {row.description && <span className="text-muted block truncate text-xs">{row.description}</span>}
      </span>
    ),
  },
  {
    key: 'category',
    header: 'Category',
    sortValue: (row) => row.category,
    cell: (row) => formatLabel(row.category),
  },
  {
    key: 'manufacturerPartNumber',
    header: 'Part number',
    sortValue: (row) => row.manufacturerPartNumber,
    cell: (row) => <span className="tabular text-xs">{orNotRecorded(row.manufacturerPartNumber)}</span>,
  },
  {
    key: 'unitCost',
    header: 'Unit cost',
    align: 'right',
    sortValue: (row) => row.unitCost,
    cell: (row) => formatMoney(row.unitCost),
  },
  {
    key: 'totalOnHand',
    header: 'On hand',
    align: 'right',
    sortValue: (row) => row.totalOnHand,
    cell: (row) => (
      <span className="text-heading font-medium">
        {formatQuantity(row.totalOnHand)}
        {unitSuffix(row.unit)}
      </span>
    ),
  },
  {
    key: 'status',
    header: 'Status',
    // Low stock first when sorted ascending: the rows somebody has to do something about.
    sortValue: (row) => (row.isBelowMinimum ? 0 : row.isActive ? 1 : 2),
    cell: (row) =>
      !row.isActive ? (
        <StatusPill status="Discontinued" />
      ) : row.isBelowMinimum ? (
        <StatusPill status="Low stock" />
      ) : (
        <StatusPill status="In stock" />
      ),
  },
];
