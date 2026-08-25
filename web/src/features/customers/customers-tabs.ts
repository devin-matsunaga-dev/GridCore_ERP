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
];
