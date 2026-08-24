import { describe, expect, it } from 'vitest';
import { navigation, navigationItems } from './navigation';

describe('sidebar navigation', () => {
  it('matches the sections in docs/Design.png, in order', () => {
    expect(navigation.map((section) => section.title)).toEqual([null, 'Operations', 'Enterprise', 'Reports']);
  });

  it('matches the reference dashboard item for item', () => {
    expect(navigation.map((section) => section.items.map((item) => item.label))).toEqual([
      ['Home'],
      [
        'Work Management',
        'Outage Management',
        'Field Operations',
        'Assets',
        'Network',
        'Monitoring',
      ],
      ['Finance', 'Procurement', 'Inventory', 'Billing & Payments', 'People', 'Customers'],
      ['Dashboards', 'Reports', 'Analytics'],
    ]);
  });

  /** Failure path: two items on one route would make the active state ambiguous. */
  it('gives every item its own route', () => {
    const routes = navigationItems.map((item) => item.to);

    expect(new Set(routes).size).toBe(routes.length);
  });

  it('gives every item an icon', () => {
    expect(navigationItems.every((item) => typeof item.icon === 'function' || typeof item.icon === 'object')).toBe(true);
  });
});
