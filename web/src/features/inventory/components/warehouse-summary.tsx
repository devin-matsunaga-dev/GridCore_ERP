import { TriangleAlert, Warehouse as WarehouseIcon } from 'lucide-react';
import type { Warehouse } from '@/api/inventory';
import { Card } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { formatCount } from '@/lib/format';
import { cn } from '@/lib/utils';

/**
 * One store per island. The warehouse list already carries `linesHeld` and `linesBelowMinimum`, so
 * this summarises the three depots without fetching the catalogue — which is exactly why WP-1.4 put
 * those counts on the response.
 *
 * Clicking a card filters the catalogue below to that store; clicking it again clears the filter.
 */
export function WarehouseSummary({
  warehouses,
  isLoading,
  selectedId,
  onSelect,
}: {
  warehouses: Warehouse[] | undefined;
  isLoading: boolean;
  selectedId: string;
  onSelect: (warehouseId: string) => void;
}) {
  if (isLoading) {
    return (
      <div className="grid gap-6 sm:grid-cols-[repeat(2,minmax(0,1fr))] xl:grid-cols-[repeat(3,minmax(0,1fr))]">
        {[0, 1, 2].map((key) => (
          <Skeleton key={key} className="h-[104px] w-full rounded-card" />
        ))}
      </div>
    );
  }

  if (!warehouses || warehouses.length === 0) return null;

  return (
    <div className="grid gap-6 sm:grid-cols-[repeat(2,minmax(0,1fr))] xl:grid-cols-[repeat(3,minmax(0,1fr))]">
      {warehouses.map((warehouse) => {
        const selected = warehouse.id === selectedId;

        return (
          <Card key={warehouse.id} className={cn('transition-shadow', selected && 'border-primary')}>
            <button
              type="button"
              // Toggling: pressing the selected store again is how a filter this small is cleared.
              onClick={() => onSelect(selected ? '' : warehouse.id)}
              aria-pressed={selected}
              className="rounded-card flex w-full items-start gap-3.5 px-5 py-4 text-left"
            >
              <span
                className={cn(
                  'flex size-10 shrink-0 items-center justify-center rounded-full',
                  selected ? 'bg-primary text-primary-foreground' : 'bg-primary-soft text-primary',
                )}
              >
                <WarehouseIcon className="size-5" strokeWidth={1.75} aria-hidden="true" />
              </span>

              <div className="min-w-0 flex-1">
                <p className="text-heading truncate text-[15px] font-semibold">{warehouse.name}</p>
                <p className="text-muted truncate text-xs">
                  {warehouse.code}
                  {warehouse.location && ` · ${warehouse.location}`}
                  {!warehouse.isActive && ' · closed'}
                </p>
                <p className="text-body tabular mt-2 text-[13px]">
                  {formatCount(warehouse.linesHeld)} lines held
                </p>
              </div>

              {warehouse.linesBelowMinimum > 0 && (
                <span className="bg-danger-soft text-danger rounded-pill inline-flex shrink-0 items-center gap-1 px-2 py-0.5 text-xs font-medium">
                  <TriangleAlert className="size-3" strokeWidth={2} aria-hidden="true" />
                  {formatCount(warehouse.linesBelowMinimum)} low
                </span>
              )}
            </button>
          </Card>
        );
      })}
    </div>
  );
}
