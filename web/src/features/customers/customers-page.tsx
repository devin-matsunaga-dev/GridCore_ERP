import { UserPlus, Users } from 'lucide-react';
import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router';
import {
  customerClasses,
  customerStatuses,
  useCustomerSearch,
  useCustomers,
  type Customer,
  type CustomerClass,
  type CustomerSearchHit,
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
import { matchDetail, matchLabel } from './search-match';
import { orNotRecorded } from '@/components/registry/detail-list';

/**
 * The customer registry. A row opens the 360° page rather than a drawer: a customer fans out into
 * service accounts, premises and — from WP-2.x — meters and bills, which is more than a panel over
 * the table can hold.
 *
 * **The search field is the CSR search** (WP-2.9). Empty, the registry lists customers as it always
 * has; with a term in it, the same field runs the five-kind search — account number, meter number,
 * phone, name, service address — and the table gains a column saying which one answered. One box,
 * because two boxes on one desk means the narrower one gets typed into and a rep concludes the
 * system cannot find people by their phone number.
 *
 * The two queries are an either/or, never both: `useCustomerSearch` is disabled without a term and
 * `useCustomers` is disabled with one. The status and class selects go to whichever is running, so
 * the filters mean the same thing in both modes.
 */
export function CustomersPage() {
  const navigate = useNavigate();

  const [search, setSearch] = useState('');
  const [status, setStatus] = useState<CustomerStatus | ''>('');
  const [customerClass, setCustomerClass] = useState<CustomerClass | ''>('');

  const term = useDebouncedValue(search);
  const filters = { search: term, status, class: customerClass };

  const searching = term.trim() !== '';

  const listQuery = useCustomers(filters, !searching);
  const searchQuery = useCustomerSearch(filters, searching);

  const query = searching ? searchQuery : listQuery;

  // The rows are plain customers either way — the search returns them whole, which is the point of
  // the endpoint's shape. Why each one matched is carried beside the list rather than folded into
  // the row, so every existing column keeps taking a Customer and nothing had to be rewritten to
  // make room for one more.
  const rows = searching ? searchQuery.data?.hits.map((hit) => hit.customer) : listQuery.data;

  const matches = useMemo(
    () => new Map((searchQuery.data?.hits ?? []).map((hit) => [hit.customer.id, hit] as const)),
    [searchQuery.data],
  );

  const shownColumns = useMemo(
    () => (searching ? [...columns, matchedOnColumn(matches)] : columns),
    [searching, matches],
  );

  const table = useTableState({ rows, columns: shownColumns });

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
        columns={shownColumns}
        table={table}
        rowKey={(row) => row.id}
        isLoading={query.isPending}
        error={query.error}
        onRetry={() => void query.refetch()}
        returnedRows={rows?.length}
        onRowActivate={(row) => void navigate(`/customers/${row.id}`)}
        filters={
          <FilterBar>
            <SearchField
              label="Search customers"
              placeholder="Name, account number, phone, address, meter…"
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
              searching
                ? `Nothing in the register matches "${term}". Try fewer words, or the number off their bill or meter.`
                : filtered
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

/**
 * The one column a search earns: why this row is in the list.
 *
 * Appended to the registry's own columns rather than swapping them out, so a rep watching the table
 * as they type sees the columns they were reading a moment ago with one more beside them — not a
 * different table. Sorted by match kind, which is the host's precedence order, so sorting on it
 * groups account-number hits above meter hits above name hits rather than alphabetising the labels.
 */
function matchedOnColumn(matches: ReadonlyMap<string, CustomerSearchHit>): Column<Customer> {
  return {
    key: 'matchedOn',
    header: 'Matched on',
    align: 'right',
    sortValue: (row) => matches.get(row.id)?.matchedOn ?? '',
    cell: (row) => {
      const match = matches.get(row.id);

      return (
        match && (
          <span className="block min-w-0">
            <span className="text-muted block truncate text-[11px] font-medium tracking-wide uppercase">
              {matchLabel(match)}
            </span>
            <span className="text-body tabular block truncate text-xs">{matchDetail(match)}</span>
          </span>
        )
      );
    },
  };
}
