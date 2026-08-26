import { describe, expect, it } from 'vitest';
import { bill, paidBill } from '@/test/revenue-cycle-fixtures';
import { depositEntry, depositLedger } from '@/test/registry-fixtures';
import {
  applicableAmount,
  billsADepositCouldSettle,
  depositKindLabel,
  depositKindTone,
  depositStanding,
  depositStandingLabel,
  depositStandingTone,
  sortDepositEntries,
} from './deposits';

describe('depositStanding', () => {
  it('separates a deposit that was never taken from one that is short', () => {
    // Two different conversations. "Nothing held" is a waived or uncollected deposit; "short" is a
    // part-payment at the counter with a balance still to come — and a screen showing both as
    // short would send a rep chasing a customer who was never asked.
    expect(depositStanding(depositLedger({ balance: 0, shortfallAmount: 450 }))).toBe('none');
    expect(depositStanding(depositLedger({ balance: 200, shortfallAmount: 250 }))).toBe('short');
  });

  it('treats a balance that exactly meets the schedule as covered', () => {
    // The boundary. Compared in cents, so 450 against 450 is met rather than short by a rounding
    // artefact — which is what a naive `>=` on parsed decimals would produce often enough to notice.
    expect(depositStanding(depositLedger({ balance: 450, assessedAmount: 450 }))).toBe('covered');
    expect(depositStanding(depositLedger({ balance: 449.99, assessedAmount: 450 }))).toBe('short');
  });

  it('treats more than the schedule asks as covered rather than as its own state', () =>
    // A customer holding more than the schedule is not a case a rep has to act on. WP-2.12 does not
    // cap a collection, so this happens legitimately.
    expect(depositStanding(depositLedger({ balance: 600, assessedAmount: 450 }))).toBe('covered'));

  it('reads a shortfall as a warning, never as an error', () => {
    // A deposit below the schedule is ordinary — a part-payment, or money spent on a bill. Rendering
    // it in the danger colour would make every part-paid customer look like a problem.
    expect(depositStandingTone('short')).toBe('warning');
    expect(depositStandingTone('covered')).toBe('success');
    expect(depositStandingTone('none')).toBe('neutral');

    expect(depositStandingLabel('short')).toBe('Below the schedule');
  });
});

describe('depositKindLabel', () => {
  it('names each movement in the words a rep would use', () => {
    expect(depositKindLabel('Collected')).toBe('Collected');
    expect(depositKindLabel('Applied')).toBe('Applied to bill');
    expect(depositKindLabel('Refunded')).toBe('Refunded');
  });

  it('does not colour a refund as a failure', () =>
    // A refund is the lifecycle finishing, not something going wrong.
    expect(depositKindTone('Refunded')).toBe('neutral'));
});

describe('billsADepositCouldSettle', () => {
  it('offers only bills the host would actually accept', () => {
    // A select whose choices produce 409s is worse than a select with fewer choices. The host
    // refuses a draft, a cancelled bill and one already settled, so none of them is offered.
    const draft = bill({ id: 'bill-draft', billNumber: 'BIL-000002', status: 'Draft', issuedOn: null });
    const cancelled = bill({ id: 'bill-cancelled', billNumber: 'BIL-000003', status: 'Cancelled' });
    const settled = paidBill({ id: 'bill-paid', billNumber: 'BIL-000004' });
    const owed = bill();

    expect(billsADepositCouldSettle([draft, cancelled, settled, owed]).map((row) => row.id)).toEqual([owed.id]);
  });

  it('leaves out a bill whose balance is already nothing even while it reads as outstanding', () =>
    // Belt and braces against a stale window: the balance is the figure a deposit is measured
    // against, and offering a bill with nothing owing would be offering a movement of zero.
    expect(billsADepositCouldSettle([bill({ balance: 0 })])).toEqual([]));

  it('puts the newest bill first, so the one that just arrived is the default choice', () => {
    const older = bill({ id: '0192f000-0000-7000-8000-000000000a01', billNumber: 'BIL-000001' });
    const newer = bill({ id: '0192f000-0000-7000-8000-000000000a02', billNumber: 'BIL-000002' });

    expect(billsADepositCouldSettle([older, newer]).map((row) => row.billNumber)).toEqual([
      'BIL-000002',
      'BIL-000001',
    ]);
  });
});

describe('applicableAmount', () => {
  it('is whichever of the deposit and the bill runs out first', () => {
    // Both ceilings are real and the host enforces both. Working out the smaller one here is what
    // lets the form show the number before a rep types over it.
    expect(applicableAmount(450, 63.62)).toBe(63.62);
    expect(applicableAmount(20, 63.62)).toBe(20);
  });

  it('is exact on amounts a float would spoil', () =>
    // 0.1 + 0.2 is a poor reason to tell a rep a fully covered bill has a cent left on it.
    expect(applicableAmount(0.1 + 0.2, 5)).toBe(0.3));
});

describe('sortDepositEntries', () => {
  it('reads newest first', () => {
    const first = depositEntry({ id: 'entry-1', recordedAt: '2026-02-11T00:30:00+00:00' });
    const second = depositEntry({ id: 'entry-2', kind: 'Refunded', recordedAt: '2026-03-11T00:30:00+00:00' });

    expect(sortDepositEntries([first, second]).map((entry) => entry.id)).toEqual(['entry-2', 'entry-1']);
  });

  it('breaks a tie on the id rather than leaving it to sort stability', () => {
    // Two movements recorded in the same millisecond — an intake that collects a deposit and a
    // correction moments later — would otherwise reshuffle between renders. The lesson
    // `buildCustomerTimeline` carries, applied to a shorter list.
    const at = '2026-02-11T00:30:00+00:00';
    const a = depositEntry({ id: 'entry-a', recordedAt: at });
    const b = depositEntry({ id: 'entry-b', recordedAt: at });

    expect(sortDepositEntries([a, b]).map((entry) => entry.id)).toEqual(['entry-b', 'entry-a']);
    expect(sortDepositEntries([b, a]).map((entry) => entry.id)).toEqual(['entry-b', 'entry-a']);
  });

  it('does not mutate what it was given', () => {
    const entries = [depositEntry({ id: 'entry-1' }), depositEntry({ id: 'entry-2', recordedAt: '2026-03-11T00:30:00+00:00' })];

    sortDepositEntries(entries);

    expect(entries.map((entry) => entry.id)).toEqual(['entry-1', 'entry-2']);
  });
});
