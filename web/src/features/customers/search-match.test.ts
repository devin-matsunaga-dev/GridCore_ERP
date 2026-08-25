import { describe, expect, it } from 'vitest';
import type { CustomerSearchHit } from '@/api/customers';
import { customer } from '@/test/registry-fixtures';
import { matchDetail, matchLabel } from './search-match';

/**
 * How a search result explains itself, tested without rendering anything (CONVENTIONS.md ⚡).
 *
 * Note what is NOT here: classification, normalisation and ranking. Those are the host's and arrive
 * already decided, so a twin of them in the browser would be a second implementation to keep in
 * step — the opposite call to the intake wizard's, and for the opposite reason.
 */

function hit(overrides: Partial<CustomerSearchHit> = {}): CustomerSearchHit {
  return {
    customer: customer(),
    matchedOn: 'Name',
    isExact: false,
    matchedValue: 'Songsong Bakery',
    serviceAccountCount: 1,
    serviceAccountNumber: 'A-000012',
    serviceAddress: '12 Beach St, Songsong, Rota',
    meterNumber: null,
    ...overrides,
  };
}

describe('the matched-on label', () => {
  it('names every way of matching', () => {
    expect(matchLabel(hit({ matchedOn: 'AccountNumber' }))).toBe('Account number');
    expect(matchLabel(hit({ matchedOn: 'MeterNumber' }))).toBe('Meter number');
    expect(matchLabel(hit({ matchedOn: 'Phone' }))).toBe('Phone');
    expect(matchLabel(hit({ matchedOn: 'Name' }))).toBe('Name');
    expect(matchLabel(hit({ matchedOn: 'Address' }))).toBe('Service address');
  });

  it('says so when the whole field matched', () => {
    // Worth saying out loud: it is the difference between reading the next row and stopping.
    expect(matchLabel(hit({ matchedOn: 'AccountNumber', isExact: true }))).toBe('Exact account number');
  });
});

describe('the matched-on detail', () => {
  it('quotes the value that matched', () => {
    expect(matchDetail(hit({ matchedOn: 'Phone', matchedValue: '670-285-1234' }))).toBe('670-285-1234');
    expect(matchDetail(hit({ matchedOn: 'MeterNumber', matchedValue: 'MTR-000007' }))).toBe('MTR-000007');
  });

  it('shows the premise instead when the match was the name already in the row', () => {
    // Repeating the name in a column beside the name column tells a rep nothing they cannot see.
    expect(matchDetail(hit({ matchedOn: 'Name' }))).toBe('12 Beach St, Songsong, Rota');
  });

  it('counts the accounts rather than picking one of them', () => {
    // The host deliberately sends neither address for a customer with two open accounts; choosing
    // one here would be inventing a fact it declined to state.
    expect(
      matchDetail(hit({ matchedOn: 'Name', serviceAddress: null, serviceAccountCount: 2 })),
    ).toBe('2 service accounts');
  });

  it('has something to say for a customer with no premise at all', () => {
    expect(matchDetail(hit({ matchedOn: 'Name', serviceAddress: null, serviceAccountCount: 0 }))).toBe('—');
  });
});
