import { ClipboardCheck } from 'lucide-react';
import { useMemo, useState } from 'react';
import {
  serviceApplicationStatuses,
  serviceTypeLabel,
  serviceTypes,
  useServiceApplications,
  type ServiceApplication,
  type ServiceApplicationStatus,
  type ServiceType,
} from '@/api/customers';
import type { Column } from '@/components/registry/data-table';
import { EmptyState } from '@/components/registry/empty-state';
import { ClearFilters, FilterBar, FilterSelect, SearchField } from '@/components/registry/filter-bar';
import { PageHeader } from '@/components/registry/page-header';
import { RegistryTableCard } from '@/components/registry/registry-table-card';
import { TabNav } from '@/components/registry/tab-nav';
import { useTableState } from '@/components/registry/table-state';
import { Button } from '@/components/ui/button';
import { StatusPill } from '@/components/ui/status';
import { formatCount, formatDate } from '@/lib/format';
import { useDebouncedValue } from '@/lib/use-debounced-value';
import { customersTabs } from '../customers-tabs';
import { ApplicationReviewDrawer } from './application-review-drawer';
import {
  applicationStatusLabel,
  applicationStatusTone,
  applicationTypeLabel,
  checklistProgress,
  sortApplications,
} from './applications';

/**
 * The review desk (WP-2.18): every request for service the utility has been asked to look at.
 *
 * **It opens on the queue, not on the register.** A desk's question is "what is waiting for me",
 * and a screen that opened on every application ever filed would make a reviewer filter before they
 * could start. The toggle is one click away for the times the question is "what did we do with that
 * one in June".
 *
 * A row opens a drawer rather than a page, the call the premise and service-account registries
 * already made: a reviewer works down a list, and losing it between applications is what makes a
 * queue tiring to work.
 */
export function ApplicationQueuePage() {
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState<ServiceApplicationStatus | ''>('');
  const [serviceType, setServiceType] = useState<ServiceType | ''>('');
  const [openOnly, setOpenOnly] = useState(true);
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const query = useServiceApplications({
    search: useDebouncedValue(search),
    status,
    serviceType,

    // A named status is narrower than "still open" and the two would fight: asking for both
    // Approved and openOnly is a query that can only ever be empty.
    openOnly: openOnly && status === '',
  });

  const rows = useMemo(() => sortApplications(query.data ?? []), [query.data]);
  const table = useTableState({ rows, columns });

  // Read out of the freshly fetched list rather than held as an object, so the drawer redraws
  // itself after an upload or a decision instead of showing the row as it was when it was clicked.
  const selected = rows.find((row) => row.id === selectedId) ?? null;

  const filtered = search !== '' || status !== '' || serviceType !== '' || !openOnly;

  return (
    <div className="space-y-6">
      <PageHeader
        title="Service applications"
        subtitle="Requests for service, reviewed before an account is opened."
        actions={<TabNav items={customersTabs} />}
      />

      <RegistryTableCard
        label="Service applications"
        columns={columns}
        table={table}
        rowKey={(row) => row.id}
        isLoading={query.isPending}
        error={query.error}
        onRetry={() => void query.refetch()}
        returnedRows={query.data?.length}
        onRowActivate={(row) => setSelectedId(row.id)}
        isRowActive={(row) => row.id === selectedId}
        filters={
          <FilterBar>
            <SearchField
              label="Search applications"
              placeholder="Application number…"
              value={search}
              onChange={setSearch}
            />
            <FilterSelect
              label="Status"
              anyLabel="Any status"
              value={status}
              onChange={setStatus}
              options={serviceApplicationStatuses}
            />
            <FilterSelect
              label="Service"
              anyLabel="All services"
              value={serviceType}
              onChange={setServiceType}
              options={serviceTypes}
            />
            <Button
              variant={openOnly ? 'primary' : 'secondary'}
              size="sm"
              aria-pressed={openOnly}
              disabled={status !== ''}
              onClick={() => setOpenOnly((current) => !current)}
            >
              Queue only
            </Button>
            <ClearFilters
              show={filtered}
              onClear={() => {
                setSearch('');
                setStatus('');
                setServiceType('');
                setOpenOnly(true);
              }}
            />
            <p className="text-muted tabular ml-auto text-[13px]">
              {query.isPending ? '' : `${formatCount(table.totalRows)} shown`}
            </p>
          </FilterBar>
        }
        empty={
          <EmptyState
            icon={ClipboardCheck}
            title={filtered ? 'No applications match those filters' : 'Nothing waiting to be reviewed'}
            message={
              filtered
                ? 'Try a broader search, or clear the filters to see every application.'
                : 'Applications appear here as they are filed. Approving one is what opens the service account.'
            }
          />
        }
      />

      <ApplicationReviewDrawer application={selected} onClose={() => setSelectedId(null)} />
    </div>
  );
}

const columns: Column<ServiceApplication>[] = [
  {
    // The activation target, so the button a reviewer opens the drawer with is named by the number
    // they quote to the applicant — and not by "Residential connection", which half the queue says.
    key: 'applicationNumber',
    header: 'Application',
    primary: true,
    sortValue: (row) => row.applicationNumber,
    cell: (row) => <span className="text-heading tabular text-[13px] font-medium">{row.applicationNumber}</span>,
  },
  {
    key: 'type',
    header: 'Applied for',
    wide: true,
    sortValue: (row) => row.type,
    cell: (row) => (
      <span className="block min-w-0">
        <span className="text-heading block truncate font-medium">{applicationTypeLabel(row.type)}</span>
        <span className="text-muted block truncate text-xs">{serviceTypeLabel(row.serviceType)}</span>
      </span>
    ),
  },
  {
    key: 'documents',
    header: 'Documents',
    // Sorted on what is OUTSTANDING rather than on what has arrived, so the applications a reviewer
    // can actually finish sort together. "3 of 3" and "0 of 2" are both complete-looking numbers
    // until you read them.
    sortValue: (row) => row.missingDocuments.length,
    cell: (row) => {
      const { satisfied, required } = checklistProgress(row);

      return (
        <span className={row.isDocumentationComplete ? 'text-body tabular' : 'text-warning tabular'}>
          {satisfied} of {required}
        </span>
      );
    },
  },
  {
    key: 'requestedOn',
    header: 'Supply wanted',
    sortValue: (row) => row.requestedOn,
    cell: (row) => <span className="text-muted">{formatDate(row.requestedOn)}</span>,
  },
  {
    key: 'status',
    header: 'Status',
    sortValue: (row) => row.status,
    cell: (row) => (
      <StatusPill status={applicationStatusLabel(row.status)} tone={applicationStatusTone(row.status)} />
    ),
  },
  {
    key: 'submittedAt',
    header: 'Filed',
    align: 'right',
    sortValue: (row) => row.submittedAt,
    cell: (row) => <span className="text-muted">{formatDate(row.submittedAt)}</span>,
  },
];
