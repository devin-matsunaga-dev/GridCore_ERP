import { UserPlus, Users } from 'lucide-react';
import { useState } from 'react';
import { useNavigate } from 'react-router';
import {
  customerClasses,
  customerStatuses,
  useCustomers,
  type Customer,
  type CustomerClass,
  type CustomerStatus,
} from '@/api/customers';
import { EmptyState } from '@/components/registry/empty-state';
import { ClearFilters, FilterBar, FilterSelect, SearchField } from '@/components/registry/filter-bar';
import { PageHeader } from '@/components/registry/page-header';
import { RegistryTableCard } from '@/components/registry/registry-table-card';
import { TabNav } from '@/components/registry/tab-nav';
import { Button } from '@/components/ui/button';
import type { Column } from '@/components/registry/data-table';
import { useTableState } from '@/components/registry/table-state';
import { StatusPill } from '@/components/ui/status';
import { formatCount, formatDate, formatLabel, formatMoney } from '@/lib/format';
import { useDebouncedValue } from '@/lib/use-debounced-value';
import { customersTabs } from './customers-tabs';
import { orNotRecorded } from '@/components/registry/detail-list';

/**
 * The customer registry. A row opens the 360° page rather than a drawer: a customer fans out into
 * service accounts, premises and — from WP-2.x — meters and bills, which is more than a panel over
 * the table can hold.
 */
export function CustomersPage() {
  const navigate = useNavigate();

  const [search, setSearch] = useState('');
  const [status, setStatus] = useState<CustomerStatus | ''>('');
  const [customerClass, setCustomerClass] = useState<CustomerClass | ''>('');

  const filters = { search: useDebouncedValue(search), status, class: customerClass };
  const query = useCustomers(filters);

  const table = useTableState({ rows: query.data, columns });

  const filtered = search !== '' || status !== '' || customerClass !== '';

  return (
    <div className="space-y-6">
      <PageHeader
        title="Customers"
        subtitle="Everyone the utility bills, and where they are served."
        actions={
          <>
            <TabNav items={customersTabs} />
            {/* The intake wizard's way in. An action rather than a third tab: registering somebody
                is something a clerk does, not a register they browse. */}
            <Button onClick={() => void navigate('/customers/new')}>
              <UserPlus aria-hidden="true" />
              Register customer
            </Button>
          </>
        }
      />

      <RegistryTableCard
        label="Customers"
        columns={columns}
        table={table}
        rowKey={(row) => row.id}
        isLoading={query.isPending}
        error={query.error}
        onRetry={() => void query.refetch()}
        returnedRows={query.data?.length}
        onRowActivate={(row) => void navigate(`/customers/${row.id}`)}
        filters={
          <FilterBar>
            <SearchField
              label="Search customers"
              placeholder="Name, number, email…"
              value={search}
              onChange={setSearch}
            />
            <FilterSelect
              label="Status"
              anyLabel="All statuses"
              value={status}
              onChange={setStatus}
              options={customerStatuses}
            />
            <FilterSelect
              label="Class"
              anyLabel="All classes"
              value={customerClass}
              onChange={setCustomerClass}
              options={customerClasses}
            />
            <ClearFilters
              show={filtered}
              onClear={() => {
                setSearch('');
                setStatus('');
                setCustomerClass('');
              }}
            />
            <p className="text-muted ml-auto text-[13px] tabular">
              {query.isPending ? '' : `${formatCount(table.totalRows)} shown`}
            </p>
          </FilterBar>
        }
        empty={
          <EmptyState
            icon={Users}
            title={filtered ? 'No customers match those filters' : 'No customers registered yet'}
            message={
              filtered
                ? 'Try a broader search, or clear the filters to see the whole registry.'
                : 'Customers appear here once they are registered.'
            }
          />
        }
      />
    </div>
  );
}

const columns: Column<Customer>[] = [
  {
    key: 'accountNumber',
    header: 'Number',
    sortValue: (row) => row.accountNumber,
    cell: (row) => <span className="text-muted tabular text-xs font-medium">{row.accountNumber}</span>,
  },
  {
    key: 'name',
    header: 'Name',
    wide: true,
    primary: true,
    sortValue: (row) => row.name,
    cell: (row) => (
      <span className="block min-w-0">
        <span className="text-heading block truncate font-medium">{row.name}</span>
        {row.contactName && <span className="text-muted block truncate text-xs">{row.contactName}</span>}
      </span>
    ),
  },
  {
    key: 'contact',
    header: 'Contact',
    cell: (row) => (
      <div className="min-w-0">
        <p className="truncate">{orNotRecorded(row.email)}</p>
        {row.phone && <p className="text-muted truncate text-xs">{row.phone}</p>}
      </div>
    ),
  },
  {
    key: 'class',
    header: 'Class',
    sortValue: (row) => row.class,
    cell: (row) => formatLabel(row.class),
  },
  {
    key: 'status',
    header: 'Status',
    sortValue: (row) => row.status,
    cell: (row) => <StatusPill status={formatLabel(row.status)} />,
  },
  {
    key: 'depositHeld',
    header: 'Deposit',
    align: 'right',
    sortValue: (row) => row.depositHeld,
    cell: (row) => formatMoney(row.depositHeld),
  },
  {
    key: 'registeredAt',
    header: 'Registered',
    align: 'right',
    sortValue: (row) => row.registeredAt,
    cell: (row) => <span className="text-muted">{formatDate(row.registeredAt)}</span>,
  },
];
