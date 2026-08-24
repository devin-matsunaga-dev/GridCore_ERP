import { describe, expect, it } from 'vitest';
import { navigationItems } from '@/components/shell/navigation';
import { moduleRoutes, placeholderRoutes } from './routes';

describe('module routes', () => {
  it('gives every nav destination except Home a route', () => {
    const destinations = navigationItems.map((item) => item.to).filter((to) => to !== '/');
    const paths = new Set(moduleRoutes.map((route) => route.path));

    expect(destinations.every((to) => paths.has(to))).toBe(true);
  });

  /** WP-1.5's three areas are real screens now; a placeholder left behind would silently win. */
  it('no longer serves a placeholder for the registries this work package built', () => {
    const placeholders = placeholderRoutes.map((route) => route.path);

    expect(placeholders).not.toContain('/customers');
    expect(placeholders).not.toContain('/assets');
    expect(placeholders).not.toContain('/inventory');
  });

  it('still serves placeholders for the areas their work packages have not reached', () => {
    const placeholders = placeholderRoutes.map((route) => route.path);

    expect(placeholders).toContain('/work-orders');
    expect(placeholders).toContain('/billing');
    expect(placeholders).toContain('/finance');
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
