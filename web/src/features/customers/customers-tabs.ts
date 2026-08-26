import type { TabNavItem } from '@/components/registry/tab-nav';

/**
 * The Customers area's screens. Service locations have no sidebar entry of their own — a premise is
 * something a customer is served at, so it lives beside them rather than becoming a fourteenth nav
 * destination (which would be a DESIGN.md change, not a screen).
 *
 * There is deliberately no Search tab (WP-2.9). The CSR search is the registry's own search field,
 * not a screen beside it: two boxes on one desk means the narrower one gets typed into, and a rep
 * concludes the system cannot find people by their phone number.
 */
export const customersTabs: TabNavItem[] = [
  { label: 'Customers', to: '/customers', end: true },
  { label: 'Service locations', to: '/customers/locations' },
  // The review desk (WP-2.18). Beside the registries rather than a fifteenth nav destination, for
  // the reason service locations are: an application is a request to become one of these customers
  // at one of these premises, so it belongs on the same desk and not in the sidebar.
  { label: 'Applications', to: '/customers/applications' },
];
