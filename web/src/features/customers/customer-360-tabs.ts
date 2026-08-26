import type { TabNavItem } from '@/components/registry/tab-nav';

/**
 * The 360° page's tabs.
 *
 * Pure, so what the strip contains and what a URL segment means are testable without a DOM — the
 * same call every other piece of this page's logic made.
 *
 * **They are ROUTES, not local state**, which is the call `TabNav` already made for the Customers
 * area: a rep can send somebody a link to the bills of an account they are both looking at, the
 * back button walks the tabs, and the sidebar's Customers entry stays lit throughout because every
 * one of these sits under `/customers`.
 */

/**
 * The tabs, in the order they read. `summary` is the one a fresh page opens on.
 *
 * `deposit` sits with the money (WP-2.12), after the payments it is an alternative to and before
 * the feed that reports on all of it. `notes` (WP-2.13) sits beside `contacts` at the front, with
 * the other things that are about the *person* rather than about their money — it is what a rep
 * reads while the customer is still on the telephone, so it belongs before the registers rather than
 * after them. Adding a tab here adds a route, so an id is part of the URL vocabulary: renaming one
 * breaks every link a rep has sent.
 */
export const customer360TabIds = [
  'summary',
  'contacts',
  'notes',
  'bills',
  'payments',
  'deposit',
  'timeline',
  'work-orders',
] as const;
export type Customer360TabId = (typeof customer360TabIds)[number];

/** The tab a bare `/customers/{id}` shows. */
export const defaultCustomer360Tab: Customer360TabId = 'summary';

const tabLabels: Record<Customer360TabId, string> = {
  summary: 'Summary',
  contacts: 'Contacts',
  notes: 'Notes',
  bills: 'Bills',
  payments: 'Payments',
  deposit: 'Deposit',
  timeline: 'Timeline',
  'work-orders': 'Work orders',
};

/**
 * The strip for one customer.
 *
 * Summary is the customer's own URL rather than `/summary`, so the link a rep copies off the
 * registry and the link they copy off this page are the same link. It carries `end` for that
 * reason: without it every child route would light the first tab up as well as its own.
 */
export function customer360Tabs(customerId: string): TabNavItem[] {
  const base = `/customers/${customerId}`;

  return customer360TabIds.map((id) => ({
    label: tabLabels[id],
    to: id === defaultCustomer360Tab ? base : `${base}/${id}`,
    end: id === defaultCustomer360Tab,
  }));
}

/**
 * What a URL segment means, or `undefined` when it means nothing.
 *
 * A missing segment is the default tab; an unrecognised one is **not** — the page redirects to the
 * customer instead of quietly rendering the summary under a URL that says `bils`. A tab strip with
 * nothing highlighted while content is on screen is the state that would otherwise result, and it
 * reads as a bug in the strip rather than as a typo in the address bar.
 */
export function resolveCustomer360Tab(segment: string | undefined): Customer360TabId | undefined {
  if (segment === undefined) return defaultCustomer360Tab;

  return customer360TabIds.find((id) => id === segment);
}
