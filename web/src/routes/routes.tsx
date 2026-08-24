import { navigationItems } from '@/components/shell/navigation';
import { ModulePlaceholder } from './module-placeholder';

/** Which work package fills each area in — surfaced in the placeholder so the shell is honest. */
const owners: Record<string, string> = {
  '/work-orders': 'WP-3.1',
  '/outages': 'WP-3.5',
  '/field-operations': 'WP-3.2',
  '/assets': 'WP-1.3',
  '/network': 'WP-4.3',
  '/monitoring': 'WP-5.4',
  '/finance': 'WP-4.2',
  '/procurement': 'WP-4.1',
  '/inventory': 'WP-1.4',
  '/billing': 'WP-2.3',
  '/people': 'WP-4.5',
  '/customers': 'WP-1.1',
  '/dashboards': 'WP-4.3',
  '/reports': 'WP-4.3',
  '/analytics': 'WP-4.3',
};

/** Every nav destination except Home, which the dashboard owns. */
export const placeholderRoutes = navigationItems
  .filter((item) => item.to !== '/')
  .map((item) => ({
    path: item.to,
    element: <ModulePlaceholder title={item.label} icon={item.icon} owner={owners[item.to] ?? 'a later work package'} />,
  }));
