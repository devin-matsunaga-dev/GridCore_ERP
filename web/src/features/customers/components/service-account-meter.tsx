import { Gauge } from 'lucide-react';
import type { Meter } from '@/api/metering';
import { Skeleton } from '@/components/ui/skeleton';
import { StatusPill } from '@/components/ui/status';
import { formatDate, formatLabel, formatQuantity } from '@/lib/format';

/**
 * The meter measuring the premise a service account is served at.
 *
 * Derived **through the premise**, never through the account (owner's call, WP-2.1). A meter is
 * fitted to a place: the service drop and the meter board stay when the occupant moves out, so a
 * meter has no account of its own and the account number beside it is display context the page
 * assembles — exactly the way the premise itself is resolved on this page.
 *
 * At most one meter can be fitted at a premise (`ux_meters_service_location`), so this is one
 * record or none. A premise with no meter is an ordinary state, not an error: a connection can be
 * requested and an account opened before a crew has been out.
 */
export function ServiceAccountMeter({
  meter,
  isPending,
}: {
  meter: Meter | undefined;
  isPending: boolean;
}) {
  return (
    <div>
      <p className="text-muted text-[11px] font-medium tracking-[0.06em] uppercase">Meter</p>

      {isPending && !meter ? (
        <Skeleton className="mt-2 h-5 w-56" />
      ) : meter ? (
        <div className="mt-2 space-y-2">
          <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
            <Gauge className="text-muted size-3.5 shrink-0" strokeWidth={1.75} aria-hidden="true" />
            <span className="text-body tabular text-[13px] font-medium">{meter.meterNumber}</span>
            <span className="text-muted text-[13px]">{formatLabel(meter.type)}</span>
            <StatusPill status={formatLabel(meter.status)} />
          </div>

          <dl className="text-[13px]">
            <div className="flex gap-2">
              <dt className="text-muted">Fitted</dt>
              <dd className="text-body">{meter.installedAt ? formatDate(meter.installedAt) : 'Not recorded'}</dd>
            </div>
            {meter.installationReading !== null && (
              <div className="flex gap-2">
                <dt className="text-muted">Reading when fitted</dt>
                <dd className="text-body tabular">{formatQuantity(meter.installationReading)}</dd>
              </div>
            )}
          </dl>
        </div>
      ) : (
        <p className="text-muted mt-2 text-[13px]">
          No meter fitted at this premise.
        </p>
      )}
    </div>
  );
}
