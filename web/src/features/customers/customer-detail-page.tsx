import { ArrowLeft } from 'lucide-react';
import { useMemo } from 'react';
import { Link, Navigate, useParams } from 'react-router';
import { useBills } from '@/api/billing';
import {
  useCustomer,
  useCustomerContacts,
  useCustomerProfile,
  useServiceAccountHistories,
  useServiceAccounts,
  useServiceLocationsByIds,
} from '@/api/customers';
import { registryWindow } from '@/api/registry';
import { useMetersByLocationIds } from '@/api/metering';
import { usePayments } from '@/api/payments';
import { DetailList, orNotRecorded } from '@/components/registry/detail-list';
import { ErrorState } from '@/components/registry/error-state';
import { PageHeader } from '@/components/registry/page-header';
import { TabNav } from '@/components/registry/tab-nav';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { StatusPill, toneFor } from '@/components/ui/status';
import { formatDate, formatLabel, formatMoney } from '@/lib/format';
import { buildCustomerTimeline, customerBalance } from './customer-360';
import { customer360Tabs, resolveCustomer360Tab } from './customer-360-tabs';
import { CustomerAccountsCard } from './components/customer-accounts-card';
import { CustomerContactsCard } from './components/customer-contacts-card';
import { CustomerProfileCard } from './components/customer-profile-card';
import { CustomerBillsCard } from './components/customer-bills-card';
import { CustomerPaymentsCard } from './components/customer-payments-card';
import { CustomerSummaryRow } from './components/customer-summary-row';
import { CustomerTimelineCard } from './components/customer-timeline-card';
import { CustomerWorkOrdersCard } from './components/customer-work-orders-card';

/**
 * The 360° customer page: the single pane a rep works from, as a summary and four tabs.
 *
 * **Five modules' worth of rows are assembled here by service, and nothing is joined.** The
 * customer, its accounts and each account's transitions come from Customers; each premise is
 * fetched by id from the same module; the meter measuring each premise from Metering; the bills
 * from Billing; the payments from Payments. Not one of these queries names another module's table,
 * and the browser is where they meet — the shape WP-1.5 established and WP-2.9 kept.
 *
 * **Every query lives here, at the page, and none of them is per-tab.** That is deliberate: the
 * summary's balance is worked out from the bills and its last-payment tile from the payments, so
 * both are needed before a rep has touched a tab — and having fetched them, making the tabs
 * re-fetch on every click would be slower and no more correct. Each still owns its own loading and
 * error state, which is what WORK_PACKAGES.md's "sections lazy-load independently" is actually
 * about: the accounts table renders while bills are in flight, and a 403 on the payments register
 * leaves every other tab exactly where it was.
 *
 * The balance and the timeline are worked out in `customer-360.ts`, which is pure. What a customer
 * owes and what order things happened in are the two claims on this page a rep would dispute, and
 * both are tested without a DOM.
 */
export function CustomerDetailPage() {
  const { customerId, tab } = useParams<{ customerId: string; tab: string }>();
  const enabled = Boolean(customerId);

  const active = resolveCustomer360Tab(tab);

  const customer = useCustomer(customerId);
  const accounts = useServiceAccounts({ customerId }, enabled);

  const premiseIds = (accounts.data ?? []).map((account) => account.serviceLocationId);
  const accountIds = (accounts.data ?? []).map((account) => account.id);

  const locations = useServiceLocationsByIds(premiseIds);
  const meters = useMetersByLocationIds(premiseIds);
  const histories = useServiceAccountHistories(accountIds);

  // The bills window is asked for WITH its adjustments. A list row otherwise carries the running
  // `adjustmentTotal` and no entries — enough for the balance, which is a total, and not enough for
  // the timeline, which needs the day each correction was made.
  const bills = useBills(
    { customerId, limit: registryWindow, includeAdjustments: true },
    enabled,
  );
  const payments = usePayments({ customerId, limit: registryWindow }, enabled);

  // The contacts tab's two queries, here at the page with every other one: switching to a tab
  // issues no request (WP-2.10's call), and each still owns its own loading and error state.
  const contacts = useCustomerContacts(customerId);
  const profile = useCustomerProfile(customerId);

  const balance = useMemo(() => customerBalance(bills.data ?? []), [bills.data]);

  const timeline = useMemo(
    () =>
      buildCustomerTimeline({
        accounts: accounts.data ?? [],
        historyByAccountId: histories.byAccountId,
        bills: bills.data ?? [],
        payments: payments.data ?? [],
      }),
    [accounts.data, histories.byAccountId, bills.data, payments.data],
  );

  // An unrecognised segment is a typo, not a tab. Sending it back to the customer keeps the URL
  // honest — the alternative is summary content under `/bils` with nothing in the strip lit up.
  if (customerId && active === undefined) {
    return <Navigate to={`/customers/${customerId}`} replace />;
  }

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

      <TabNav items={customer360Tabs(record.id)} />

      {active === 'summary' && (
        <div className="space-y-6">
          <CustomerSummaryRow
            customer={record}
            balance={balance}
            payments={payments.data ?? []}
            isPending={bills.isPending || payments.isPending}
          />

          <Card>
            <CardHeader>
              <CardTitle>Customer record</CardTitle>
            </CardHeader>
            <CardContent>
              {/*
                A description list, not a table: these are one subject's labelled fields, which is
                the one shape on this page that is not a register of like rows.
              */}
              <DetailList
                columns={2}
                items={[
                  { label: 'Contact', value: orNotRecorded(record.contactName) },
                  {
                    label: 'Deposit held',
                    value: <span className="tabular">{formatMoney(record.depositHeld)}</span>,
                  },
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

          <section className="space-y-4" aria-labelledby="service-accounts-heading">
            <h3 id="service-accounts-heading" className="text-heading text-lg font-semibold">
              Service accounts
            </h3>

            <CustomerAccountsCard
              accounts={accounts.data ?? []}
              locations={locations.byId}
              isLocationPending={locations.isPending}
              meters={meters.byLocationId}
              isMeterPending={meters.isPending}
              histories={histories.byAccountId}
              isHistoryPending={histories.isPending}
              isLoading={accounts.isPending}
              error={accounts.isError ? accounts.error : undefined}
              onRetry={() => void accounts.refetch()}
            />
          </section>
        </div>
      )}

      {active === 'contacts' && customerId && (
        <div className="space-y-6">
          <CustomerProfileCard
            customer={record}
            profile={profile.data}
            isLoading={profile.isPending}
            error={profile.isError ? profile.error : undefined}
            onRetry={() => void profile.refetch()}
          />

          <CustomerContactsCard
            customerId={customerId}
            contacts={contacts.data ?? []}
            isLoading={contacts.isPending}
            error={contacts.isError ? contacts.error : undefined}
            onRetry={() => void contacts.refetch()}
          />
        </div>
      )}

      {active === 'bills' && (
        <CustomerBillsCard
          bills={bills.data ?? []}
          isLoading={bills.isPending}
          error={bills.isError ? bills.error : undefined}
          onRetry={() => void bills.refetch()}
        />
      )}

      {active === 'payments' && (
        <CustomerPaymentsCard
          payments={payments.data ?? []}
          isLoading={payments.isPending}
          error={payments.isError ? payments.error : undefined}
          onRetry={() => void payments.refetch()}
        />
      )}

      {active === 'timeline' && (
        <CustomerTimelineCard
          entries={timeline}
          // The feed is only as complete as its weakest source, so it reports the weakest. Waiting
          // on any of them is still loading; any of them refusing is an error, because a feed
          // silently missing one module's entries reads as a feed, not as a failure.
          isLoading={
            accounts.isPending || bills.isPending || payments.isPending || histories.isPending
          }
          error={
            accounts.isError || bills.isError || payments.isError
              ? (accounts.error ?? bills.error ?? payments.error)
              : undefined
          }
          onRetry={() => {
            void accounts.refetch();
            void bills.refetch();
            void payments.refetch();
          }}
        />
      )}

      {active === 'work-orders' && <CustomerWorkOrdersCard />}
    </div>
  );
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
