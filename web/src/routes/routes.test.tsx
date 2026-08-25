import { describe, expect, it } from 'vitest';
import { navigationItems } from '@/components/shell/navigation';
import { moduleRoutes, placeholderRoutes } from './routes';

describe('module routes', () => {
  it('gives every nav destination except Home a route', () => {
    const destinations = navigationItems.map((item) => item.to).filter((to) => to !== '/');
    const paths = new Set(moduleRoutes.map((route) => route.path));

    expect(destinations.every((to) => paths.has(to))).toBe(true);
  });

  /** A placeholder left behind on a built area would silently win over the screen. */
  it('no longer serves a placeholder for any area that has a screen', () => {
    const placeholders = placeholderRoutes.map((route) => route.path);

    expect(placeholders).not.toContain('/customers');
    expect(placeholders).not.toContain('/assets');
    expect(placeholders).not.toContain('/inventory');
    // WP-2.7: Billing & Payments is the revenue-cycle walk now, and its `owners` entry — stale at
    // WP-2.3 ever since that work package shipped no screens — is gone with it.
    expect(placeholders).not.toContain('/billing');
  });

  it('still serves placeholders for the areas their work packages have not reached', () => {
    const placeholders = placeholderRoutes.map((route) => route.path);

    expect(placeholders).toContain('/work-orders');
    expect(placeholders).toContain('/finance');
    expect(placeholders).toContain('/procurement');
  });

  /**
   * Detail routes live *under* their nav path so the sidebar entry stays active on them —
   * `NavLink` matches by prefix for everything but Home.
   */
  it('nests the customer detail routes under the Customers destination', () => {
    const paths = moduleRoutes.map((route) => route.path);

    expect(paths).toContain('/customers/:customerId');
    expect(paths).toContain('/customers/locations');
    expect(paths.filter((path) => path.startsWith('/customers')).every((path) => path.startsWith('/customers'))).toBe(true);
  });

  /** Failure path: two routes on one path would make which element renders arbitrary. */
  it('declares each path once', () => {
    const paths = moduleRoutes.map((route) => route.path);

    expect(new Set(paths).size).toBe(paths.length);
  });
});
