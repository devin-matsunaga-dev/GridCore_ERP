import { FileText } from 'lucide-react';
import { useState } from 'react';
import { serviceTypeLabel, type ServiceAccount, type ServiceAccountHistoryEntry, type ServiceLocation } from '@/api/customers';
import type { Meter } from '@/api/metering';
import type { Column } from '@/components/registry/data-table';
import { EmptyState } from '@/components/registry/empty-state';
import { RegistryTableCard } from '@/components/registry/registry-table-card';
import { useTableState } from '@/components/registry/table-state';
import { Skeleton } from '@/components/ui/skeleton';
import { StatusPill } from '@/components/ui/status';
import { formatDate, formatLabel } from '@/lib/format';
import { sortAccounts } from '../customer-360';
import { ServiceAccountDrawer } from './service-account-drawer';

/**
 * Every service account this customer holds, as a table.
 *
 * A table rather than a grid of cards: these are like-shaped records with the same six facts each,
 * which is the definition of a row, and a customer with six accounts was six screens of scrolling
 * before. DESIGN.md's registry density applies — the premise absorbs the slack, the numerics are
 * right-aligned and the status is a pill inline. The rest of an account is a drawer, so opening one
 * does not lose the list.
 *
 * The rows arrive pre-sorted by `sortAccounts` — open above closed — which is the order a rep wants
 * before they have touched a column header. Choosing a column takes over from there.
 */
export function CustomerAccountsCard({
  accounts,
  locations,
  isLocationPending,
  meters,
  isMeterPending,
  histories,
  isHistoryPending,
  isLoading,
  error,
  onRetry,
}: {
  accounts: readonly ServiceAccount[];
  locations: ReadonlyMap<string, ServiceLocation>;
  isLocationPending: boolean;
  meters: ReadonlyMap<string, Meter>;
  isMeterPending: boolean;
  histories: ReadonlyMap<string, readonly ServiceAccountHistoryEntry[]>;
  isHistoryPending: boolean;
  isLoading: boolean;
  error: unknown;
  onRetry: () => void;
}) {
  const [openId, setOpenId] = useState<string | null>(null);

  const columns = accountColumns(locations, isLocationPending, meters, isMeterPending);
  const table = useTableState({ rows: sortAccounts(accounts), columns });

  const open = accounts.find((account) => account.id === openId) ?? null;

  return (
    <>
      <RegistryTableCard
        columns={columns}
        table={table}
        rowKey={(account) => account.id}
        label="Service accounts"
        isLoading={isLoading}
        error={error}
        onRetry={onRetry}
        onRowActivate={(account) => setOpenId(account.id)}
        isRowActive={(account) => account.id === openId}
        returnedRows={accounts.length}
        empty={
          <EmptyState
            icon={FileText}
            title="No service accounts yet"
            message="An account pairs this customer with a premise. Both ends are fixed when it is opened, so moving house is a new account."
          />
        }
      />

      <ServiceAccountDrawer
        account={open}
        location={open ? locations.get(open.serviceLocationId) : undefined}
        isLocationPending={isLocationPending}
        meter={open ? meters.get(open.serviceLocationId) : undefined}
        isMeterPending={isMeterPending}
        history={(open && histories.get(open.id)) ?? []}
        isHistoryPending={isHistoryPending}
        onClose={() => setOpenId(null)}
      />
    </>
  );
}

/**
 * Built per render because two of the columns close over maps that fill in separately — the premise
 * and the meter arrive after the account does, and each cell renders a skeleton until its own
 * request lands rather than holding the whole table back.
 */
function accountColumns(
  locations: ReadonlyMap<string, ServiceLocation>,
  isLocationPending: boolean,
  meters: ReadonlyMap<string, Meter>,
  isMeterPending: boolean,
): Column<ServiceAccount>[] {
  return [
    {
      key: 'accountNumber',
      header: 'Account',
      sortValue: (account) => account.accountNumber,
      cell: (account) => (
        <span className="text-muted tabular text-xs font-medium">{account.accountNumber}</span>
      ),
    },
    {
      key: 'premise',
      header: 'Premise',
      wide: true,
      primary: true,
      sortValue: (account) => locations.get(account.serviceLocationId)?.formattedAddress,
      cell: (account) => {
        const location = locations.get(account.serviceLocationId);

        if (!location) {
          return isLocationPending ? (
            <Skeleton className="h-3.5 w-48" />
          ) : (
            <span className="text-muted">Premise {account.serviceLocationId}</span>
          );
        }

        return (
          <span className="block min-w-0">
            <span className="text-heading block truncate font-medium">{location.formattedAddress}</span>
            <span className="text-muted tabular block truncate text-xs">{location.locationCode}</span>
          </span>
        );
      },
    },
    {
      key: 'serviceType',
      header: 'Service',
      // What the account is FOR (WP-2.17). Beside the premise rather than at the end, because a
      // customer holding three accounts at one address is reading down this column to tell them
      // apart — every other cell in those three rows says the same thing.
      sortValue: (account) => account.serviceType,
      cell: (account) => (
        <span className="text-body text-xs font-medium">{serviceTypeLabel(account.serviceType)}</span>
      ),
    },
    {
      key: 'meter',
      header: 'Meter',
      // Through the premise, never through the account: a meter is fitted to a place and holds no
      // account of its own (WP-2.1). A premise with no meter is an ordinary state — a connection
      // can be requested and an account opened before a crew has been out.
      sortValue: (account) => meters.get(account.serviceLocationId)?.meterNumber,
      cell: (account) => {
        // An unmetered account never has one and never will (WP-2.17), so it says so rather than
        // showing the em dash that means "not fitted yet" on every other row.
        if (!account.isMetered) return <span className="text-muted text-xs">Unmetered</span>;

        const meter = meters.get(account.serviceLocationId);

        if (meter) return <span className="tabular text-xs">{meter.meterNumber}</span>;

        return isMeterPending ? <Skeleton className="h-3.5 w-20" /> : <span className="text-muted">—</span>;
      },
    },
    {
      key: 'openedAt',
      header: 'Opened',
      sortValue: (account) => account.openedAt,
      cell: (account) => <span className="text-muted">{formatDate(account.openedAt)}</span>,
    },
    {
      key: 'serviceStartedAt',
      header: 'Service from',
      sortValue: (account) => account.serviceStartedAt,
      cell: (account) => (
        <span className="text-muted">
          {account.serviceStartedAt ? formatDate(account.serviceStartedAt) : '—'}
        </span>
      ),
    },
    {
      key: 'status',
      header: 'Status',
      sortValue: (account) => account.status,
      cell: (account) => <StatusPill status={formatLabel(account.status)} />,
    },
  ];
}
