import { describe, expect, it } from 'vitest';
import {
  customer360TabIds,
  customer360Tabs,
  defaultCustomer360Tab,
  resolveCustomer360Tab,
} from './customer-360-tabs';

const customerId = '0192f000-0000-7000-8000-000000000001';

describe('customer360Tabs', () => {
  it('builds one link per tab, under this customer', () => {
    const tabs = customer360Tabs(customerId);

    expect(tabs.map((tab) => tab.label)).toEqual([
      'Summary',
      'Contacts',
      'Bills',
      'Payments',
      'Deposit',
      'Timeline',
      'Work orders',
    ]);

    for (const tab of tabs) {
      expect(tab.to.startsWith(`/customers/${customerId}`)).toBe(true);
    }
  });

  /**
   * The link a rep copies off the registry and the link they copy off this page are the same link,
   * so Summary is the customer's own URL rather than `/summary`.
   */
  it('points Summary at the customer itself, and matches it exactly', () => {
    const [summary, contacts] = customer360Tabs(customerId);

    expect(summary?.to).toBe(`/customers/${customerId}`);
    expect(summary?.end).toBe(true);

    // Without `end` on the first tab, every child route would light it up as well as its own.
    expect(contacts?.to).toBe(`/customers/${customerId}/contacts`);
    expect(contacts?.end).toBeFalsy();
  });

  /** The sidebar's Customers entry stays lit because every tab lives under `/customers`. */
  it('keeps every tab beneath the Customers nav path', () => {
    expect(customer360Tabs(customerId).every((tab) => tab.to.startsWith('/customers/'))).toBe(true);
  });
});

describe('resolveCustomer360Tab', () => {
  it('treats a missing segment as the default tab', () => {
    expect(resolveCustomer360Tab(undefined)).toBe(defaultCustomer360Tab);
    expect(defaultCustomer360Tab).toBe('summary');
  });

  it('resolves every tab it offers', () => {
    for (const id of customer360TabIds) {
      expect(resolveCustomer360Tab(id)).toBe(id);
    }
  });

  /**
   * Failure path: an unrecognised segment is a typo, not a tab. Answering `undefined` is what lets
   * the page redirect rather than render the summary under a URL that says `bils` — which would
   * leave the strip with nothing highlighted and read as a bug in the strip.
   */
  it('refuses a segment it does not know', () => {
    expect(resolveCustomer360Tab('bils')).toBeUndefined();
    expect(resolveCustomer360Tab('')).toBeUndefined();
    expect(resolveCustomer360Tab('Bills')).toBeUndefined();
  });
});
