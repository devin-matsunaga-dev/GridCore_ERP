import { MapPin } from 'lucide-react';
import { Link } from 'react-router';
import type { ServiceAccount, ServiceLocation } from '@/api/customers';
import { DetailList, orNotRecorded } from '@/components/registry/detail-list';
import { Timeline, type TimelineEntry } from '@/components/registry/timeline';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { StatusPill, toneFor } from '@/components/ui/status';
import { formatDate, formatLabel } from '@/lib/format';

/**
 * One service account on the 360° page: the premise it is served at, the dates that answer "since
 * when is this live", and the account's own history — which is the service record an agent reads
 * back on the phone, not the audit trail.
 */
export function ServiceAccountCard({
  account,
  location,
  isLocationPending,
}: {
  account: ServiceAccount;
  location: ServiceLocation | undefined;
  isLocationPending: boolean;
}) {
  return (
    <Card>
      <CardHeader>
        <div className="min-w-0">
          <CardTitle className="tabular">{account.accountNumber}</CardTitle>
          <div className="text-muted mt-1.5 flex min-w-0 items-center gap-1.5 text-[13px]">
            <MapPin className="size-3.5 shrink-0" strokeWidth={1.75} aria-hidden="true" />
            {isLocationPending && !location ? (
              <Skeleton className="h-3.5 w-48" />
            ) : location ? (
              <Link
                to="/customers/locations"
                className="hover:text-primary truncate transition-colors"
                title={location.formattedAddress}
              >
                {location.formattedAddress}
              </Link>
            ) : (
              <span>Premise {account.serviceLocationId}</span>
            )}
          </div>
        </div>
        <StatusPill status={formatLabel(account.status)} />
      </CardHeader>

      <CardContent className="space-y-5">
        <DetailList
          items={[
            { label: 'Opened', value: formatDate(account.openedAt) },
            { label: 'Premise code', value: location?.locationCode ?? <Skeleton className="h-3.5 w-16" /> },
            { label: 'Service started', value: orNotRecorded(account.serviceStartedAt && formatDate(account.serviceStartedAt)) },
            { label: 'Service ended', value: orNotRecorded(account.serviceEndedAt && formatDate(account.serviceEndedAt)) },
            { label: 'Last change', value: orNotRecorded(account.statusChangedAt && formatDate(account.statusChangedAt)) },
            { label: 'Reason', value: orNotRecorded(account.statusReason) },
          ]}
        />

        {/*
          The transitions the aggregate would allow, shown as the disabled/enabled buttons DESIGN.md
          asks for. They are not wired to anything: WP-1.5 is a read WP, and start/stop/close are
          POST sub-resources whose owner is the work package that gives an agent a reason to press
          them. Rendering them read-only still proves the state machine reaches the screen.
        */}
        <div>
          <p className="text-muted text-[11px] font-medium tracking-[0.06em] uppercase">
            Allowed transitions
          </p>
          <div className="mt-2 flex flex-wrap gap-1.5">
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
        </div>

        {account.history.length > 0 && (
          <div>
            <p className="text-muted mb-3 text-[11px] font-medium tracking-[0.06em] uppercase">
              Account history
            </p>
            <Timeline entries={historyEntries(account)} />
          </div>
        )}
      </CardContent>
    </Card>
  );
}

/** Newest first — the opening line, whose `fromStatus` is null, reads as "Account opened". */
function historyEntries(account: ServiceAccount): TimelineEntry[] {
  return account.history
    .toReversed()
    .map((entry) => ({
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
