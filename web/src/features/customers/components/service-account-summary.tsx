import { Link } from 'react-router';
import type { ServiceAccount } from '@/api/customers';
import { StatusPill } from '@/components/ui/status';
import { formatDate, formatLabel } from '@/lib/format';
import { cn } from '@/lib/utils';

/**
 * One service account as a compact row — the account number, where it stands and the dates that
 * answer "since when is this live". Used wherever accounts are listed beside something else: the
 * 360° page's premises and the service-location drawer.
 */
export function ServiceAccountSummary({
  account,
  secondary,
  to,
  className,
}: {
  account: ServiceAccount;
  /** The other end of the pairing — the premise on a customer page, the customer on a premise. */
  secondary?: React.ReactNode;
  to?: string;
  className?: string;
}) {
  const body = (
    <>
      <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
        <span className="text-heading tabular text-[13px] font-semibold">{account.accountNumber}</span>
        <StatusPill status={formatLabel(account.status)} />
      </div>
      {secondary && <p className="text-body mt-1 text-[13px]">{secondary}</p>}
      <p className="text-muted mt-1 text-xs">
        Opened {formatDate(account.openedAt)}
        {account.serviceStartedAt && ` · service from ${formatDate(account.serviceStartedAt)}`}
        {account.serviceEndedAt && ` · stopped ${formatDate(account.serviceEndedAt)}`}
      </p>
    </>
  );

  const classes = cn('border-border rounded-field block border px-3.5 py-3', className);

  return to ? (
    <Link to={to} className={cn(classes, 'hover:bg-canvas transition-colors')}>
      {body}
    </Link>
  ) : (
    <div className={classes}>{body}</div>
  );
}
