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
 * the feed that reports on all of it. `documents` (WP-2.14) sits after all three registers it draws
 * on — a statement is composed from the bills, the payments and the deposit, so a rep reads them and
 * then sends them; and it sits before `timeline`, which reports rather than produces. `notes` (WP-2.13) sits beside `contacts` at the front, with
 * the other things that are about the *person* rather than about their money — it is what a rep
 * reads while the customer is still on the telephone, so it belongs before the registers rather than
 * after them.
 *
 * `delinquency` (WP-2.19) sits after `charges` and before `documents`: it is a read of what the
 * bills, the fees and the deposit add up to when nobody has paid — so every register it draws on is
 * to its left, and the statement a rep sends about it is to its right.
 *
 * `arrangements` (WP-2.20) sits immediately after `delinquency`, because it is what a rep does
 * NEXT: they read what is past due and how close the account is to being cut off, and then they
 * arrange payment instead. Reversing the two would put the remedy before the diagnosis.
 *
 * `charges` (WP-2.16's screen, shipped with WP-2.18) sits with the money, after `deposit` and
 * before `documents`: a fee is the non-consumption half of what the customer is asked for, so it
 * reads beside the bills and the deposit rather than beside the person — and a statement composed
 * from all of them belongs after it.
 *
 * `transitions` (WP-2.15) sits AFTER everything it might depend on and before the feed that reports
 * on all of it: a rep reads the deposit before they transfer it and runs a statement before they
 * close an account, and both of those tabs are to its left. It is the last thing done on a call
 * rather than the first, which is also where it reads.
 *
 * Adding a tab here adds a route, so an id is part of the URL vocabulary: renaming one breaks every
 * link a rep has sent.
 */
export const customer360TabIds = [
  'summary',
  'contacts',
  'notes',
  'bills',
  'payments',
  'deposit',
  'charges',
  'delinquency',
  'arrangements',
  'documents',
  'transitions',
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
  charges: 'Charges',
  delinquency: 'Delinquency',
  arrangements: 'Arrangements',
  documents: 'Documents',
  transitions: 'Transitions',
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
