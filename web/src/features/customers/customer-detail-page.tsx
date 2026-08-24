import { ArrowLeft, FileText } from 'lucide-react';
import { Link, useParams } from 'react-router';
import {
  useCustomer,
  useServiceAccounts,
  useServiceLocationsByIds,
  type ServiceAccount,
} from '@/api/customers';
import { DetailList, orNotRecorded } from '@/components/registry/detail-list';
import { EmptyState } from '@/components/registry/empty-state';
import { ErrorState } from '@/components/registry/error-state';
import { PageHeader } from '@/components/registry/page-header';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { StatusPill, toneFor } from '@/components/ui/status';
import { formatCount, formatDate, formatLabel, formatMoney } from '@/lib/format';
import { ServiceAccountCard } from './components/service-account-card';

/**
 * The 360° customer page: who they are, then every service account they hold, each with the premise
 * it is served at and its own history. A page rather than a drawer, because this is the view that
 * WP-2.x hangs meters, bills and payments off — the fan-out is the point.
 *
 * Three modules' worth of rows are assembled here by *service*, never by a join: the customer, the
 * accounts filtered by `?customerId=`, and each premise fetched by id.
 */
export function CustomerDetailPage() {
  const { customerId } = useParams<{ customerId: string }>();

  const customer = useCustomer(customerId);
  const accounts = useServiceAccounts({ customerId }, Boolean(customerId));
  const locations = useServiceLocationsByIds(
    (accounts.data ?? []).map((account) => account.serviceLocationId),
  );

  if (customer.isError) {
    return (
      <div className="space-y-6">
        <BackLink />
        <Card>
          <ErrorState error={customer.error} onRetry={() => void customer.refetch()} />
        </Card>
      </div>
    );
  }

  if (customer.isPending || !customer.data) {
    return (
      <div className="space-y-6">
        <BackLink />
        <Skeleton className="h-9 w-72" />
        <Card>
          <CardContent className="space-y-4 pt-6">
            <Skeleton className="h-4 w-full" />
            <Skeleton className="h-4 w-2/3" />
          </CardContent>
        </Card>
      </div>
    );
  }

  const record = customer.data;
  const held = accounts.data ?? [];

  return (
    <div className="space-y-6">
      <BackLink />

      <PageHeader
        title={record.name}
        subtitle={
          <span className="flex flex-wrap items-center gap-x-2 gap-y-1">
            <span className="tabular">{record.accountNumber}</span>
            <span aria-hidden="true">·</span>
            <span>{formatLabel(record.class)}</span>
          </span>
        }
        actions={<StatusPill status={formatLabel(record.status)} />}
      />

      <Card>
        <CardHeader>
          <CardTitle>Customer record</CardTitle>
        </CardHeader>
        <CardContent>
          <DetailList
            columns={2}
            items={[
              { label: 'Contact', value: orNotRecorded(record.contactName) },
              { label: 'Deposit held', value: <span className="tabular">{formatMoney(record.depositHeld)}</span> },
              {
                label: 'Email',
                value: record.email ? (
                  <a href={`mailto:${record.email}`} className="text-primary hover:underline">
                    {record.email}
                  </a>
                ) : (
                  orNotRecorded(null)
                ),
              },
              {
                label: 'Phone',
                value: record.phone ? (
                  <a href={`tel:${record.phone}`} className="text-primary hover:underline">
                    {record.phone}
                  </a>
                ) : (
                  orNotRecorded(null)
                ),
              },
              { label: 'Registered', value: formatDate(record.registeredAt) },
              {
                label: 'Status changed',
                value: orNotRecorded(record.statusChangedAt && formatDate(record.statusChangedAt)),
              },
              { label: 'Status reason', value: orNotRecorded(record.statusReason), wide: true },
              {
                label: 'Allowed transitions',
                wide: true,
                value:
                  record.allowedTransitions.length === 0 ? (
                    <span className="text-muted">None — closed is terminal.</span>
                  ) : (
                    <span className="flex flex-wrap gap-1.5">
                      {record.allowedTransitions.map((status) => (
                        <StatusPill key={status} status={formatLabel(status)} tone={toneFor(status)} />
                      ))}
                    </span>
                  ),
              },
            ]}
          />
        </CardContent>
      </Card>

      <section className="space-y-4">
        <div className="flex flex-wrap items-baseline justify-between gap-2">
          <h3 className="text-heading text-lg font-semibold">Service accounts</h3>
          <p className="text-muted text-[13px]">
            {accounts.isPending ? '' : `${formatCount(held.length)} on this customer`}
          </p>
        </div>

        {accounts.isError ? (
          <Card>
            <ErrorState error={accounts.error} onRetry={() => void accounts.refetch()} />
          </Card>
        ) : accounts.isPending ? (
          <Card>
            <CardContent className="space-y-3 pt-6">
              <Skeleton className="h-5 w-40" />
              <Skeleton className="h-20 w-full" />
            </CardContent>
          </Card>
        ) : held.length === 0 ? (
          <Card>
            <EmptyState
              icon={FileText}
              title="No service accounts yet"
              message="An account pairs this customer with a premise. Both ends are fixed when it is opened, so moving house is a new account."
            />
          </Card>
        ) : (
          <div className="grid gap-4 2xl:grid-cols-[repeat(2,minmax(0,1fr))]">
            {sortAccounts(held).map((account) => (
              <ServiceAccountCard
                key={account.id}
                account={account}
                location={locations.byId.get(account.serviceLocationId)}
                isLocationPending={locations.isPending}
              />
            ))}
          </div>
        )}
      </section>
    </div>
  );
}

/**
 * Open accounts first, then by account number. A disconnected supply is what somebody rang up
 * about; a closed account from four years ago is history and belongs below it.
 */
function sortAccounts(accounts: readonly ServiceAccount[]): ServiceAccount[] {
  return accounts.toSorted((left, right) => {
    const closed = Number(left.status === 'Closed') - Number(right.status === 'Closed');

    return closed || left.accountNumber.localeCompare(right.accountNumber, undefined, { numeric: true });
  });
}

function BackLink() {
  return (
    <Button variant="ghost" size="sm" className="-ml-3" asChild>
      <Link to="/customers">
        <ArrowLeft aria-hidden="true" />
        All customers
      </Link>
    </Button>
  );
}
