import { describe, expect, it } from 'vitest';
import { accountCharge, feeScheduleEntry, serviceAccount } from '@/test/registry-fixtures';
import {
  chargeStatusTone,
  chargeableAccounts,
  feeCodeLabel,
  isActionable,
  pendingTotal,
  priceOf,
  sortCharges,
} from './charges';

describe('chargeableAccounts', () => {
  it('includes a closed account, because the host does', () => {
    // A meter-test fee or an unauthorised-connection penalty is often the last thing that happens to
    // a supply, after it has been closed. Hiding those here would be the screen inventing a rule.
    const open = serviceAccount({ id: 'a', accountNumber: 'A-000002', status: 'Active' });
    const closed = serviceAccount({ id: 'b', accountNumber: 'A-000001', status: 'Closed' });

    expect(chargeableAccounts([open, closed]).map((account) => account.accountNumber)).toEqual([
      'A-000001',
      'A-000002',
    ]);
  });
});

describe('priceOf', () => {
  it('reads the figure the catalogue published for the day it was fetched for', () => {
    const schedule = [
      feeScheduleEntry({ code: 'ServiceConnection', amount: 135 }),
      feeScheduleEntry({ code: 'Reconnection', amount: 60, feeScheduleId: 'fee-2' }),
    ];

    expect(priceOf(schedule, 'Reconnection')?.amount).toBe(60);
  });

  it('answers nothing for a code the catalogue does not publish today', () => {
    // The schedule is effective-dated, so a code with no row in force is an ordinary answer rather
    // than a fault — and the form has to be able to say "no figure" instead of guessing one.
    expect(priceOf([feeScheduleEntry()], 'MeterTest')).toBeUndefined();
  });
});

describe('isActionable', () => {
  it('follows the host rather than comparing a status string', () => {
    // `Billed` is terminal on the host: correcting a billed fee is an adjustment to the bill that
    // carries it. A button offered against one would be a button that produces 409s.
    expect(isActionable(accountCharge())).toBe(true);
    expect(isActionable(accountCharge({ status: 'Billed', isPending: false }))).toBe(false);
    expect(isActionable(accountCharge({ status: 'Cancelled', isPending: false }))).toBe(false);
  });
});

describe('pendingTotal', () => {
  it('adds up what has been raised and not yet billed', () => {
    const charges = [
      accountCharge({ id: 'a', amount: 135 }),
      accountCharge({ id: 'b', amount: 50 }),
      accountCharge({ id: 'c', amount: 25, status: 'Billed', isPending: false }),
      accountCharge({ id: 'd', amount: 999, status: 'Cancelled', isPending: false }),
    ];

    expect(pendingTotal(charges)).toBe(185);
  });

  it('is zero rather than undefined when nothing is pending', () => {
    expect(pendingTotal([])).toBe(0);
  });
});

describe('sortCharges', () => {
  it('reads newest first', () => {
    const older = accountCharge({ id: 'a', raisedAt: '2026-08-20T10:00:00+00:00' });
    const newer = accountCharge({ id: 'b', raisedAt: '2026-08-27T10:00:00+00:00' });

    expect(sortCharges([older, newer]).map((row) => row.id)).toEqual(['b', 'a']);
  });

  it('is total, so two raised in the same millisecond do not depend on sort stability', () => {
    const first = accountCharge({ id: 'aaa' });
    const second = accountCharge({ id: 'bbb' });

    expect(sortCharges([first, second]).map((row) => row.id)).toEqual(['bbb', 'aaa']);
    expect(sortCharges([second, first]).map((row) => row.id)).toEqual(['bbb', 'aaa']);
  });
});

describe('labels and tones', () => {
  it('reads a fee in sentence case, with the local spelling', () => {
    expect(feeCodeLabel('UnauthorizedConnection')).toBe('Unauthorised connection');
    expect(feeCodeLabel('ReturnedPayment')).toBe('Returned payment');
  });

  it('paints a pending fee as something still to happen and a billed one as done', () => {
    expect(chargeStatusTone('Pending')).toBe('warning');
    expect(chargeStatusTone('Billed')).toBe('success');
    expect(chargeStatusTone('Cancelled')).toBe('neutral');
  });
});
