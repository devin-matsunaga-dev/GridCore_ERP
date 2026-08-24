import type { TabNavItem } from '@/components/registry/tab-nav';

/**
 * The Customers area's two registries. Service locations have no sidebar entry of their own —
 * a premise is something a customer is served at, so it lives beside them rather than becoming a
 * fourteenth nav destination (which would be a DESIGN.md change, not a screen).
 */
export const customersTabs: TabNavItem[] = [
  { label: 'Customers', to: '/customers', end: true },
  { label: 'Service locations', to: '/customers/locations' },
];
