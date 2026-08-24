import { useState } from 'react';
import {
  stockMovementTypes,
  unitSuffix,
  useStockItem,
  useStockMovements,
  warehouseNames,
  type StockItem,
  type StockMovement,
  type StockMovementType,
  type Warehouse,
} from '@/api/inventory';
import type { Column } from '@/components/registry/data-table';
import { DataTable } from '@/components/registry/data-table';
import { DetailList, orNotRecorded } from '@/components/registry/detail-list';
import { Drawer, DrawerSection } from '@/components/registry/drawer';
import { FilterSelect } from '@/components/registry/filter-bar';
import { Skeleton } from '@/components/ui/skeleton';
import { StatusPill, toneFor } from '@/components/ui/status';
import { formatDate, formatDateTime, formatLabel, formatMoney, formatQuantity } from '@/lib/format';
import { cn } from '@/lib/utils';

/**
 * A catalogue line's detail: what it is, what is on each shelf, and the ledger that explains those
 * quantities. The two are shown together on purpose — a running total nobody can trace back to the
 * movements behind it is the thing WP-1.4's append-only ledger exists to prevent.
 */
export function StockItemDrawer({
  itemId,
  warehouses,
  onClose,
}: {
  itemId: string | null;
  warehouses: Warehouse[] | undefined;
  onClose: () => void;
}) {
  const [movementType, setMovementType] = useState<StockMovementType | ''>('');
  const [warehouseId, setWarehouseId] = useState('');

  const item = useStockItem(itemId ?? undefined);
  const movements = useStockMovements(itemId ?? undefined, { movementType, warehouseId });

  if (!itemId) return null;

  const names = warehouseNames(warehouses);
  const unit = item.data ? unitSuffix(item.data.unit) : '';

  return (
    <Drawer
      open
      onClose={onClose}
      title={item.data?.name ?? 'Loading item…'}
      subtitle={
        item.data && (
          <>
            <span className="text-muted tabular text-[13px]">{item.data.itemCode}</span>
            <StatusPill status={item.data.isActive ? 'Active' : 'Discontinued'} />
            {item.data.isBelowMinimum && <StatusPill status="Low stock" />}
          </>
        )
      }
    >
      {item.isPending || !item.data ? (
        <div className="space-y-3">
          <Skeleton className="h-4 w-full" />
          <Skeleton className="h-4 w-2/3" />
          <Skeleton className="h-32 w-full" />
        </div>
      ) : (
        <div className="space-y-6">
          <DrawerSection title="Catalogue line">
            <DetailList items={catalogueItems(item.data)} />
          </DrawerSection>

          <DrawerSection title="On hand by warehouse">
            {item.data.levels.length === 0 ? (
              <p className="text-muted text-[13px]">
                No store carries this line yet. A shelf exists once something is received onto it.
              </p>
            ) : (
              <ul className="space-y-2">
                {item.data.levels.map((level) => (
                  <li
                    key={level.warehouseId}
                    className={cn(
                      'border-border rounded-field flex items-center justify-between gap-3 border px-3.5 py-2.5',
                      level.isBelowMinimum && 'border-danger/40 bg-danger-soft/40',
                    )}
                  >
                    <div className="min-w-0">
                      <p className="text-heading truncate text-[13px] font-medium">
                        {names.get(level.warehouseId) ?? 'Unknown warehouse'}
                      </p>
                      <p className="text-muted text-xs">
                        {/* Zero means nobody set a reorder level — which is not the same as "low". */}
                        {level.minimumQuantity > 0
                          ? `Reorder at ${formatQuantity(level.minimumQuantity)}${unit}`
                          : 'No reorder level set'}
                        {level.lastMovedAt && ` · last moved ${formatDate(level.lastMovedAt)}`}
                      </p>
                    </div>
                    <span
                      className={cn(
                        'tabular shrink-0 text-[15px] font-semibold',
                        level.isBelowMinimum ? 'text-danger' : 'text-heading',
                      )}
                    >
                      {formatQuantity(level.quantityOnHand)}
                      {unit}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </DrawerSection>

          <DrawerSection
            title="Stock ledger"
            action={
              <div className="flex flex-wrap gap-1.5">
                {warehouses && warehouses.length > 0 && (
                  <FilterSelect
                    label="Ledger warehouse"
                    anyLabel="All stores"
                    value={warehouseId}
                    onChange={setWarehouseId}
                    options={warehouses.map((warehouse) => warehouse.id)}
                    format={(id) => names.get(id) ?? id}
                  />
                )}
                <FilterSelect
                  label="Movement type"
                  anyLabel="All movements"
                  value={movementType}
                  onChange={setMovementType}
                  options={stockMovementTypes}
                />
              </div>
            }
          >
            {movements.isPending ? (
              <Skeleton className="h-24 w-full" />
            ) : movements.data && movements.data.length > 0 ? (
              <DataTable
                label="Stock movements"
                columns={movementColumns(unit, names)}
                rows={movements.data}
                rowKey={(row) => row.id}
                className="-mx-0 px-0"
              />
            ) : (
              <p className="text-muted text-[13px]">Nothing matches those ledger filters.</p>
            )}
          </DrawerSection>
        </div>
      )}
    </Drawer>
  );
}

function catalogueItems(item: StockItem) {
  return [
    { label: 'Category', value: formatLabel(item.category) },
    { label: 'Unit', value: formatLabel(item.unit) },
    {
      label: 'Standard cost',
      // Not a moving average: a receipt records its own cost on its ledger line and leaves this be.
      value: <span className="tabular">{formatMoney(item.unitCost)}</span>,
    },
    {
      label: 'Total on hand',
      value: (
        <span className="tabular">
          {formatQuantity(item.totalOnHand)}
          {unitSuffix(item.unit)}
        </span>
      ),
    },
    { label: 'Part number', value: orNotRecorded(item.manufacturerPartNumber) },
    { label: 'Registered', value: formatDate(item.registeredAt) },
    { label: 'Description', value: orNotRecorded(item.description), wide: true },
    {
      label: item.isActive ? 'Status note' : 'Discontinued because',
      value: orNotRecorded(item.statusReason),
      wide: true,
    },
  ];
}

/**
 * The ledger, newest first as the API returns it. `quantityChange` is signed and
 * `quantityOnHandAfter` is stamped on the line, so a reader can walk the column down and see the
 * running total arrive at what the shelf says.
 */
function movementColumns(unit: string, names: Map<string, string>): Column<StockMovement>[] {
  return [
    {
      key: 'recordedAt',
      header: 'When',
      cell: (row) => <span className="text-muted text-xs whitespace-nowrap">{formatDateTime(row.recordedAt)}</span>,
    },
    {
      key: 'movementType',
      header: 'Type',
      cell: (row) => <StatusPill status={row.movementType} tone={toneFor(row.movementType)} />,
    },
    {
      key: 'warehouse',
      header: 'Store',
      wide: true,
      cell: (row) => (
        <div className="min-w-0">
          <p className="truncate">{names.get(row.warehouseId) ?? 'Unknown'}</p>
          {(row.reference ?? row.note) && (
            <p className="text-muted truncate text-xs" title={row.note ?? row.reference ?? undefined}>
              {row.reference ?? row.note}
            </p>
          )}
        </div>
      ),
    },
    {
      key: 'quantityChange',
      header: 'Change',
      align: 'right',
      cell: (row) => (
        <span className={cn('font-medium', row.quantityChange < 0 ? 'text-danger' : 'text-success')}>
          {row.quantityChange > 0 && '+'}
          {formatQuantity(row.quantityChange)}
          {unit}
        </span>
      ),
    },
    {
      key: 'quantityOnHandAfter',
      header: 'On hand',
      align: 'right',
      cell: (row) => (
        <span className="text-heading font-medium">
          {formatQuantity(row.quantityOnHandAfter)}
          {unit}
        </span>
      ),
    },
  ];
}
