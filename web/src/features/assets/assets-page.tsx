import { Building2 } from 'lucide-react';
import { useState } from 'react';
import {
  assetClasses,
  assetConditions,
  assetStatuses,
  useAssets,
  type Asset,
  type AssetClass,
  type AssetCondition,
  type AssetStatus,
} from '@/api/assets';
import type { Column } from '@/components/registry/data-table';
import { orNotRecorded } from '@/components/registry/detail-list';
import { EmptyState } from '@/components/registry/empty-state';
import { ClearFilters, FilterBar, FilterSelect, SearchField } from '@/components/registry/filter-bar';
import { PageHeader } from '@/components/registry/page-header';
import { RegistryTableCard } from '@/components/registry/registry-table-card';
import { useTableState } from '@/components/registry/table-state';
import { StatusPill, toneFor } from '@/components/ui/status';
import { formatCount, formatDate, formatLabel } from '@/lib/format';
import { useDebouncedValue } from '@/lib/use-debounced-value';
import { AssetDrawer } from './components/asset-drawer';

/**
 * The plant register. A row opens a drawer rather than a page: what a technician wants is this
 * transformer's serial, its grade and when it was last touched, without losing the filtered list
 * they narrowed down to find it.
 */
export function AssetsPage() {
  const [search, setSearch] = useState('');
  const [assetClass, setAssetClass] = useState<AssetClass | ''>('');
  const [status, setStatus] = useState<AssetStatus | ''>('');
  const [condition, setCondition] = useState<AssetCondition | ''>('');
  const [openAssetId, setOpenAssetId] = useState<string | null>(null);

  const query = useAssets({
    search: useDebouncedValue(search),
    class: assetClass,
    status,
    condition,
  });

  const table = useTableState({ rows: query.data, columns });

  const filtered = search !== '' || assetClass !== '' || status !== '' || condition !== '';

  return (
    <div className="space-y-6">
      <PageHeader
        title="Assets"
        subtitle="Every piece of plant on the books — transformers, poles, spans, switchgear and vehicles."
      />

      <RegistryTableCard
        label="Assets"
        columns={columns}
        table={table}
        rowKey={(row) => row.id}
        isLoading={query.isPending}
        error={query.error}
        onRetry={() => void query.refetch()}
        returnedRows={query.data?.length}
        onRowActivate={(row) => setOpenAssetId(row.id)}
        isRowActive={(row) => row.id === openAssetId}
        filters={
          <FilterBar>
            <SearchField
              label="Search assets"
              placeholder="Tag, name, serial…"
              value={search}
              onChange={setSearch}
            />
            <FilterSelect
              label="Class"
              anyLabel="All classes"
              value={assetClass}
              onChange={setAssetClass}
              options={assetClasses}
              format={formatLabel}
            />
            <FilterSelect
              label="Status"
              anyLabel="All statuses"
              value={status}
              onChange={setStatus}
              options={assetStatuses}
              format={formatLabel}
            />
            <FilterSelect
              label="Condition"
              anyLabel="All conditions"
              value={condition}
              onChange={setCondition}
              options={assetConditions}
            />
            <ClearFilters
              show={filtered}
              onClear={() => {
                setSearch('');
                setAssetClass('');
                setStatus('');
                setCondition('');
              }}
            />
            <p className="text-muted tabular ml-auto text-[13px]">
              {query.isPending ? '' : `${formatCount(table.totalRows)} shown`}
            </p>
          </FilterBar>
        }
        empty={
          <EmptyState
            icon={Building2}
            title={filtered ? 'No plant matches those filters' : 'Nothing in the register yet'}
            message={
              filtered
                ? 'Try a broader search, or clear the filters to see the whole register.'
                : 'Assets appear here once they are registered.'
            }
          />
        }
      />

      <AssetDrawer assetId={openAssetId} onClose={() => setOpenAssetId(null)} />
    </div>
  );
}

const columns: Column<Asset>[] = [
  {
    key: 'assetTag',
    header: 'Tag',
    sortValue: (row) => row.assetTag,
    cell: (row) => <span className="text-muted tabular text-xs font-medium">{row.assetTag}</span>,
  },
  {
    key: 'name',
    header: 'Asset',
    wide: true,
    primary: true,
    sortValue: (row) => row.name,
    cell: (row) => (
      <span className="block min-w-0">
        <span className="text-heading block truncate font-medium">{row.name}</span>
        {row.locationNote && <span className="text-muted block truncate text-xs">{row.locationNote}</span>}
      </span>
    ),
  },
  {
    key: 'class',
    header: 'Class',
    sortValue: (row) => row.class,
    cell: (row) => formatLabel(row.class),
  },
  {
    key: 'serialNumber',
    header: 'Serial',
    // Unique when present; poles and spans carry none, and those sort to the bottom either way.
    sortValue: (row) => row.serialNumber,
    cell: (row) => <span className="tabular text-xs">{orNotRecorded(row.serialNumber)}</span>,
  },
  {
    key: 'status',
    header: 'Status',
    sortValue: (row) => row.status,
    cell: (row) => <StatusPill status={formatLabel(row.status)} />,
  },
  {
    key: 'condition',
    header: 'Condition',
    // Ordered by the enum, worst last, so sorting groups what needs attention rather than spelling.
    sortValue: (row) => assetConditions.indexOf(row.condition),
    cell: (row) => <StatusPill status={row.condition} tone={toneFor(row.condition)} />,
  },
  {
    key: 'installedOn',
    header: 'Installed',
    align: 'right',
    sortValue: (row) => row.installedOn,
    cell: (row) => <span className="text-muted">{orNotRecorded(row.installedOn && formatDate(row.installedOn))}</span>,
  },
];
