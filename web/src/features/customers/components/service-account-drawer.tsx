import { serviceTypeLabel, type ServiceAccount, type ServiceAccountHistoryEntry, type ServiceLocation } from '@/api/customers';
import type { Meter } from '@/api/metering';
import { DetailList, orNotRecorded } from '@/components/registry/detail-list';
import { Drawer, DrawerSection } from '@/components/registry/drawer';
import { Timeline, type TimelineEntry } from '@/components/registry/timeline';
import { Skeleton } from '@/components/ui/skeleton';
import { StatusPill, toneFor } from '@/components/ui/status';
import { formatDate, formatLabel } from '@/lib/format';
import { ServiceAccountMeter } from './service-account-meter';

/**
 * One service account's detail, over the accounts table on the 360° page.
 *
 * A drawer rather than a card in a grid: the account, its premise, its meter and its transitions
 * are a panel's worth of record, and a rep comparing two accounts wants the list of them on screen
 * while they do it. Same call `ServiceLocationDrawer` made for a premise.
 *
 * Three modules meet here and nothing joins them. The account comes from Customers, the premise is
 * fetched by id, the meter is looked up **by premise** — a meter is fitted to a place and holds no
 * account, so the premise is what relates the two (WP-2.1, owner's call) — and the transitions come
 * from the account's own history endpoint, because a list row carries none.
 */
export function ServiceAccountDrawer({
  account,
  location,
  isLocationPending,
  meter,
  isMeterPending,
  history,
  isHistoryPending,
  onClose,
}: {
  account: ServiceAccount | null;
  location: ServiceLocation | undefined;
  isLocationPending: boolean;
  meter: Meter | undefined;
  isMeterPending: boolean;
  history: readonly ServiceAccountHistoryEntry[];
  isHistoryPending: boolean;
  onClose: () => void;
}) {
  if (!account) return null;

  return (
    <Drawer
      open
      onClose={onClose}
      title={account.accountNumber}
      subtitle={
        <>
          <span className="text-muted text-[13px]">
            {location ? location.formattedAddress : 'Premise loading…'}
          </span>
          <StatusPill status={formatLabel(account.status)} />
          <span className="text-muted text-[13px]">{serviceTypeLabel(account.serviceType)}</span>
        </>
      }
    >
      <div className="space-y-6">
        <DrawerSection title="Account">
          <DetailList
            items={[
              {
                label: 'Service',
                value: account.isMetered
                  ? serviceTypeLabel(account.serviceType)
                  : `${serviceTypeLabel(account.serviceType)} (unmetered)`,
              },
              { label: 'Opened', value: formatDate(account.openedAt) },
              {
                label: 'Premise code',
                value: isLocationPending && !location ? <Skeleton className="h-3.5 w-16" /> : (location?.locationCode ?? orNotRecorded(null)),
              },
              {
                label: 'Service started',
                value: orNotRecorded(account.serviceStartedAt && formatDate(account.serviceStartedAt)),
              },
              {
                label: 'Service ended',
                value: orNotRecorded(account.serviceEndedAt && formatDate(account.serviceEndedAt)),
              },
              {
                label: 'Last change',
                value: orNotRecorded(account.statusChangedAt && formatDate(account.statusChangedAt)),
              },
              { label: 'Reason', value: orNotRecorded(account.statusReason) },
              {
                label: 'Premise',
                wide: true,
                value:
                  isLocationPending && !location ? (
                    <Skeleton className="h-3.5 w-56" />
                  ) : (
                    (location?.formattedAddress ?? `Premise ${account.serviceLocationId}`)
                  ),
              },
            ]}
          />
        </DrawerSection>

        <DrawerSection title="Meter">
          {account.isMetered ? (
            <ServiceAccountMeter meter={meter} isPending={isMeterPending} />
          ) : (
            // Not "no meter fitted yet" — there will never be one. Wastewater is billed a flat
            // charge and GridCore refuses to fit a revenue meter where only unmetered service is
            // taken (WP-2.17), so a screen that showed the ordinary empty state here would be
            // inviting a rep to go and look for a device nobody is going to install.
            <p className="text-muted text-[13px]">
              {serviceTypeLabel(account.serviceType)} is unmetered — no meter is fitted and no reading is taken.
            </p>
          )}
        </DrawerSection>

        {/*
          The transitions the aggregate would allow, as the enabled/disabled buttons DESIGN.md asks
          for — still not wired to anything. Start, stop and close are POST sub-resources whose
          owner is WP-2.15, which adds the reason codes they need. Rendering them read-only proves
          the state machine reaches the screen, which is WP-1.5's call and still the right one.
        */}
        <DrawerSection title="Allowed transitions">
          <div className="flex flex-wrap gap-1.5">
            {account.allowedTransitions.length === 0 ? (
              <span className="text-muted text-[13px]">
                None — {formatLabel(account.status).toLowerCase()} is terminal.
              </span>
            ) : (
              account.allowedTransitions.map((status) => (
                <StatusPill key={status} status={formatLabel(status)} tone={toneFor(status)} />
              ))
            )}
          </div>
        </DrawerSection>

        <DrawerSection title="Account history">
          {isHistoryPending && history.length === 0 ? (
            <div className="space-y-2">
              <Skeleton className="h-3.5 w-40" />
              <Skeleton className="h-3.5 w-28" />
            </div>
          ) : history.length === 0 ? (
            <p className="text-muted text-[13px]">No transitions recorded against this account.</p>
          ) : (
            <Timeline entries={historyEntries(history)} />
          )}
        </DrawerSection>
      </div>
    </Drawer>
  );
}

/**
 * Newest first — the opening line, whose `fromStatus` is null, reads as "Account opened".
 *
 * A feed rather than a table, unlike everything else on this page: one account's transitions are a
 * handful of lines about a single subject, which is what `Timeline` is for. The page-level feed
 * that merges four modules is the one that grew into a table.
 */
function historyEntries(history: readonly ServiceAccountHistoryEntry[]): TimelineEntry[] {
  return history.toReversed().map((entry) => ({
    id: entry.id,
    title: entry.fromStatus
      ? `${formatLabel(entry.fromStatus)} → ${formatLabel(entry.toStatus)}`
      : 'Account opened',
    detail: entry.reason,
    actor: entry.actorName ?? entry.actorId,
    recordedAt: entry.recordedAt,
    tone: toneFor(entry.toStatus),
  }));
}
