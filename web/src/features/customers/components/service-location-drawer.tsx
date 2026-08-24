import { useServiceAccounts, type ServiceLocation } from '@/api/customers';
import { DetailList, orNotRecorded } from '@/components/registry/detail-list';
import { Drawer, DrawerSection } from '@/components/registry/drawer';
import { Skeleton } from '@/components/ui/skeleton';
import { StatusPill } from '@/components/ui/status';
import { formatDate } from '@/lib/format';
import { ServiceAccountSummary } from './service-account-summary';

/**
 * A premise's detail. A drawer rather than a page: an address, a flag and the accounts that have
 * held it is a panel's worth of record, and losing the table behind it to see one would be worse.
 */
export function ServiceLocationDrawer({
  location,
  onClose,
}: {
  location: ServiceLocation | null;
  onClose: () => void;
}) {
  // Keyed by the premise so the accounts refetch when the drawer moves to another row.
  const accounts = useServiceAccounts(
    { serviceLocationId: location?.id },
    Boolean(location),
  );

  if (!location) return null;

  const { address } = location;

  return (
    <Drawer
      open
      onClose={onClose}
      title={location.formattedAddress}
      subtitle={
        <>
          <span className="text-muted tabular text-[13px]">{location.locationCode}</span>
          <StatusPill status={location.isActive ? 'Active' : 'Inactive'} />
        </>
      }
    >
      <div className="space-y-6">
        <DrawerSection title="Premise">
          <DetailList
            items={[
              { label: 'Address line 1', value: address.line1, wide: true },
              { label: 'Address line 2', value: orNotRecorded(address.line2), wide: true },
              { label: 'Village', value: address.city },
              { label: 'Island', value: address.region },
              { label: 'Postal code', value: orNotRecorded(address.postalCode) },
              { label: 'Country', value: address.country },
              { label: 'Description', value: orNotRecorded(location.description), wide: true },
              { label: 'Registered', value: formatDate(location.registeredAt) },
              {
                label: location.isActive ? 'Status' : 'Deactivated because',
                value: orNotRecorded(location.statusReason ?? (location.isActive ? 'Active' : null)),
              },
            ]}
          />
        </DrawerSection>

        <DrawerSection title="Service accounts at this premise">
          {accounts.isPending ? (
            <div className="space-y-2">
              <Skeleton className="h-16 w-full" />
              <Skeleton className="h-16 w-full" />
            </div>
          ) : accounts.data && accounts.data.length > 0 ? (
            <ul className="space-y-2">
              {accounts.data.map((account) => (
                <li key={account.id}>
                  <ServiceAccountSummary
                    account={account}
                    to={`/customers/${account.customerId}`}
                    secondary="Open the customer this account belongs to"
                  />
                </li>
              ))}
            </ul>
          ) : (
            <p className="text-muted text-[13px]">
              No account has ever been opened here. One open account at a time is a database rule, so
              this premise is free.
            </p>
          )}
        </DrawerSection>
      </div>
    </Drawer>
  );
}
