import type { ReactElement } from 'react';
import { navigationItems } from '@/components/shell/navigation';
import { AssetsPage } from '@/features/assets/assets-page';
import { CustomerDetailPage } from '@/features/customers/customer-detail-page';
import { CustomersPage } from '@/features/customers/customers-page';
import { ServiceLocationsPage } from '@/features/customers/service-locations-page';
import { InventoryPage } from '@/features/inventory/inventory-page';
import { ModulePlaceholder } from './module-placeholder';

/**
 * Every nav destination except Home, which the dashboard owns. An area is either built — its
 * element is here — or it is a placeholder naming the work package that fills it in, so the shell
 * stays honest about what exists.
 */

/** The areas WP-1.5 built. A key here replaces that route's placeholder. */
const registryPages: Record<string, ReactElement> = {
  '/customers': <CustomersPage />,
  '/assets': <AssetsPage />,
  '/inventory': <InventoryPage />,
};

/** Which work package fills each remaining area in — surfaced in the placeholder. */
const owners: Record<string, string> = {
  '/work-orders': 'WP-3.1',
  '/outages': 'WP-3.5',
  '/field-operations': 'WP-3.2',
  '/network': 'WP-4.3',
  '/monitoring': 'WP-5.4',
  '/finance': 'WP-4.2',
  '/procurement': 'WP-4.1',
  '/billing': 'WP-2.3',
  '/people': 'WP-4.5',
  '/dashboards': 'WP-4.3',
  '/reports': 'WP-4.3',
  '/analytics': 'WP-4.3',
};

export type ModuleRoute = { path: string; element: ReactElement };

/**
 * The sub-routes a built area adds beneath its nav destination. They live under the nav path so the
 * sidebar entry stays active on a detail page (`NavLink` matches by prefix for everything but Home).
 */
const childRoutes: ModuleRoute[] = [
  { path: '/customers/locations', element: <ServiceLocationsPage /> },
  { path: '/customers/:customerId', element: <CustomerDetailPage /> },
];

export const moduleRoutes: ModuleRoute[] = [
  ...navigationItems
    .filter((item) => item.to !== '/')
    .map((item) => ({
      path: item.to,
      element: registryPages[item.to] ?? (
        <ModulePlaceholder
          title={item.label}
          icon={item.icon}
          owner={owners[item.to] ?? 'a later work package'}
        />
      ),
    })),
  ...childRoutes,
];

/** The destinations still waiting on their work package — what the shell tests assert against. */
export const placeholderRoutes = moduleRoutes.filter((route) => !(route.path in registryPages));
