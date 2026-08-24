import { MapPin } from 'lucide-react';
import { useState } from 'react';
import {
  useServiceLocations,
  type ServiceLocation,
} from '@/api/customers';
import type { Column } from '@/components/registry/data-table';
import { orNotRecorded } from '@/components/registry/detail-list';
import { EmptyState } from '@/components/registry/empty-state';
import { ClearFilters, FilterBar, FilterSelect, SearchField } from '@/components/registry/filter-bar';
import { PageHeader } from '@/components/registry/page-header';
import { RegistryTableCard } from '@/components/registry/registry-table-card';
import { TabNav } from '@/components/registry/tab-nav';
import { useTableState } from '@/components/registry/table-state';
import { StatusPill } from '@/components/ui/status';
import { formatCount, formatDate } from '@/lib/format';
import { useDebouncedValue } from '@/lib/use-debounced-value';
import { ServiceLocationDrawer } from './components/service-location-drawer';
import { customersTabs } from './customers-tabs';

/**
 * The premise registry. The islands are the regions the seeded world uses; the filter offers them
 * as a fixed list because `?region=` is a server-side match and these three are the utility's whole
 * territory. A row opens a drawer — an address and its accounts is a panel, not a page.
 */
const islands = ['Saipan', 'Rota', 'Tinian'] as const;

const activity = ['Active', 'Inactive'] as const;
type Activity = (typeof activity)[number];

export function ServiceLocationsPage() {
  const [search, setSearch] = useState('');
  const [region, setRegion] = useState<(typeof islands)[number] | ''>('');
  const [state, setState] = useState<Activity | ''>('');
  const [selected, setSelected] = useState<ServiceLocation | null>(null);

  const query = useServiceLocations({
    search: useDebouncedValue(search),
    region,
    isActive: state === '' ? '' : state === 'Active',
  });

  const table = useTableState({ rows: query.data, columns });

  const filtered = search !== '' || region !== '' || state !== '';

  return (
    <div className="space-y-6">
      <PageHeader
        title="Service locations"
        subtitle="Every premise the utility can connect, across the three islands."
        actions={<TabNav items={customersTabs} />}
      />

      <RegistryTableCard
        label="Service locations"
        columns={columns}
        table={table}
        rowKey={(row) => row.id}
        isLoading={query.isPending}
        error={query.error}
        onRetry={() => void query.refetch()}
        returnedRows={query.data?.length}
        onRowActivate={setSelected}
        isRowActive={(row) => row.id === selected?.id}
        filters={
          <FilterBar>
            <SearchField
              label="Search service locations"
              placeholder="Address, code, village…"
              value={search}
              onChange={setSearch}
            />
            <FilterSelect
              label="Island"
              anyLabel="All islands"
              value={region}
              onChange={setRegion}
              options={islands}
            />
            <FilterSelect
              label="Availability"
              anyLabel="Active and inactive"
              value={state}
              onChange={setState}
              options={activity}
            />
            <ClearFilters
              show={filtered}
              onClear={() => {
                setSearch('');
                setRegion('');
                setState('');
              }}
            />
            <p className="text-muted tabular ml-auto text-[13px]">
              {query.isPending ? '' : `${formatCount(table.totalRows)} shown`}
            </p>
          </FilterBar>
        }
        empty={
          <EmptyState
            icon={MapPin}
            title={filtered ? 'No premises match those filters' : 'No premises registered yet'}
            message={
              filtered
                ? 'Try a broader search, or clear the filters to see the whole registry.'
                : 'Premises appear here once they are registered.'
            }
          />
        }
      />

      <ServiceLocationDrawer location={selected} onClose={() => setSelected(null)} />
    </div>
  );
}

const columns: Column<ServiceLocation>[] = [
  {
    key: 'locationCode',
    header: 'Code',
    sortValue: (row) => row.locationCode,
    cell: (row) => <span className="text-muted tabular text-xs font-medium">{row.locationCode}</span>,
  },
  {
    key: 'address',
    header: 'Address',
    wide: true,
    primary: true,
    sortValue: (row) => row.address.line1,
    cell: (row) => (
      <span className="block min-w-0">
        <span className="text-heading block truncate font-medium">{row.address.line1}</span>
        {row.description && <span className="text-muted block truncate text-xs">{row.description}</span>}
      </span>
    ),
  },
  {
    key: 'city',
    header: 'Village',
    sortValue: (row) => row.address.city,
    cell: (row) => row.address.city,
  },
  {
    key: 'region',
    header: 'Island',
    sortValue: (row) => row.address.region,
    cell: (row) => row.address.region,
  },
  {
    key: 'postalCode',
    header: 'Postal',
    cell: (row) => <span className="tabular">{orNotRecorded(row.address.postalCode)}</span>,
  },
  {
    key: 'isActive',
    header: 'Status',
    sortValue: (row) => row.isActive,
    cell: (row) => <StatusPill status={row.isActive ? 'Active' : 'Inactive'} />,
  },
  {
    key: 'registeredAt',
    header: 'Registered',
    align: 'right',
    sortValue: (row) => row.registeredAt,
    cell: (row) => <span className="text-muted">{formatDate(row.registeredAt)}</span>,
  },
];
