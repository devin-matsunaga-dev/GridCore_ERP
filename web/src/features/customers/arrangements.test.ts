import { describe, expect, it } from 'vitest';
import {
  arrangementLabel,
  arrangementTone,
  arrangeableAccounts,
  awaitsApproval,
  canPropose,
  isOverdue,
  limitFor,
  nextInstalment,
  previewSchedule,
  progressOf,
  sortArrangements,
  standingArrangement,
  willNeedApproval,
} from './arrangements';
import {
  arrangementInstalments,
  arrangementLimits,
  paymentArrangement,
  serviceAccount,
} from '@/test/registry-fixtures';

/**
 * The arrangements tab's logic (WP-2.20), with no DOM in sight.
 *
 * The pure half is where the interesting decisions are: which promise is standing, whether a rep
 * needs a supervisor, and what a schedule reads like before anything is committed.
 */
describe('the schedule preview', () => {
  it('adds up to the balance exactly, with the remainder on the last instalment', () => {
    // The host's rule, mirrored: "$33.33, $33.33, $33.34" is a column a customer can check down the
    // telephone. These are the same worked examples the .NET `ArrangementScheduleTests` uses, which
    // is what holds the two implementations to one answer.
    expect(previewSchedule(100, 0, 3).map((line) => line.amount)).toEqual([33.33, 33.33, 33.34]);
    expect(previewSchedule(120, 0, 4).map((line) => line.amount)).toEqual([30, 30, 30, 30]);
    expect(previewSchedule(999.99, 0, 12).reduce((total, line) => total + line.amount, 0)).toBeCloseTo(999.99, 2);
  });

  it('makes the down payment the first line, due today', () => {
    const lines = previewSchedule(500, 100, 4);

    expect(lines[0]).toEqual({ sequence: 1, amount: 100, isDownPayment: true });
    expect(lines.slice(1).map((line) => line.amount)).toEqual([100, 100, 100, 100]);
    expect(lines.reduce((total, line) => total + line.amount, 0)).toBe(500);
  });

  it('works in whole cents, so a schedule never fails to add up to its own total', () => {
    // 0.1 + 0.2 is not 0.3 in a browser, and a preview that showed three figures which did not sum
    // to the balance is exactly what this exists to avoid.
    const lines = previewSchedule(0.3, 0, 3);

    expect(lines.map((line) => line.amount)).toEqual([0.1, 0.1, 0.1]);
  });

  it('shows nothing where the figures do not describe a schedule', () => {
    // The host's refusal is what says WHY, in words. The preview simply stops offering a promise it
    // cannot render.
    expect(previewSchedule(0, 0, 3)).toEqual([]);
    expect(previewSchedule(200, 200, 3)).toEqual([]);
    expect(previewSchedule(200, 250, 3)).toEqual([]);
    expect(previewSchedule(200, -1, 3)).toEqual([]);
    expect(previewSchedule(200, 0, 0)).toEqual([]);
    expect(previewSchedule(0.02, 0, 3)).toEqual([]);
    expect(previewSchedule(Number.NaN, 0, 3)).toEqual([]);
  });
});

describe('which promise is standing', () => {
  it('is read off the standing rather than the recorded status', () => {
    // An arrangement that missed an instalment yesterday is still Active in the column until the
    // review run comes round — and its standing is already Broken, which is what protection turns on.
    const defaulted = paymentArrangement({ status: 'Active', standing: 'Broken' });

    expect(standingArrangement([defaulted])).toBeUndefined();
    expect(canPropose([defaulted])).toBe(true);
  });

  it('finds a proposal as well as one in force, because a second promise beside either is refused', () => {
    expect(standingArrangement([paymentArrangement({ standing: 'Proposed' })])).toBeDefined();
    expect(standingArrangement([paymentArrangement({ standing: 'Active' })])).toBeDefined();
    expect(canPropose([paymentArrangement({ standing: 'Active' })])).toBe(false);
  });

  it('treats a kept arrangement as history, so a fresh one may be offered', () => {
    expect(canPropose([paymentArrangement({ status: 'Kept', standing: 'Kept' })])).toBe(true);
  });

  it('prefers the newest where an account has been through several', () => {
    const older = paymentArrangement({ id: 'a', arrangementNumber: 'PA-000001', standing: 'Proposed' });
    const newer = paymentArrangement({ id: 'b', arrangementNumber: 'PA-000002', standing: 'Active' });

    expect(sortArrangements([older, newer])[0]).toBe(newer);
    expect(standingArrangement([older, newer])).toBe(newer);
  });
});

describe('the rep’s ceiling', () => {
  it('warns on either ceiling on its own', () => {
    // Two ceilings rather than one: a small debt spread over three years is a write-off wearing a
    // schedule's clothes.
    const residential = limitFor(arrangementLimits(), 'Residential');

    expect(willNeedApproval(residential, 1500, 6)).toBe(false);
    expect(willNeedApproval(residential, 1500.01, 6)).toBe(true);
    expect(willNeedApproval(residential, 100, 7)).toBe(true);
  });

  it('judges a commercial customer against the commercial ceiling', () => {
    const commercial = limitFor(arrangementLimits(), 'Commercial');

    expect(willNeedApproval(commercial, 4000, 12)).toBe(false);
    expect(willNeedApproval(limitFor(arrangementLimits(), 'Residential'), 4000, 12)).toBe(true);
  });

  it('warns about nothing while the ceilings have not loaded', () => {
    // A screen that guessed would tell a rep they need a supervisor when they do not.
    expect(willNeedApproval(undefined, 999999, 99)).toBe(false);
  });

  it('says an over-limit proposal is waiting on somebody else', () => {
    expect(awaitsApproval(paymentArrangement({ status: 'Proposed', requiresApproval: true }))).toBe(true);
    expect(awaitsApproval(paymentArrangement({ status: 'Proposed', requiresApproval: false }))).toBe(false);
    expect(awaitsApproval(paymentArrangement({ status: 'Active', requiresApproval: true }))).toBe(false);
  });
});

describe('the schedule as it reads', () => {
  it('points at the earliest unsettled instalment', () => {
    const arrangement = paymentArrangement({
      instalments: arrangementInstalments([
        { paidAmount: 100, outstanding: 0, isSettled: true },
      ]),
    });

    expect(nextInstalment(arrangement)?.sequence).toBe(2);
  });

  it('has nothing to point at once every instalment has arrived', () => {
    const arrangement = paymentArrangement({
      instalments: arrangementInstalments([
        { paidAmount: 100, outstanding: 0, isSettled: true },
        { paidAmount: 100, outstanding: 0, isSettled: true },
        { paidAmount: 100, outstanding: 0, isSettled: true },
      ]),
    });

    expect(nextInstalment(arrangement)).toBeUndefined();
    expect(progressOf(arrangement)).toBe(0);
  });

  it('reports how far through the schedule the customer is', () => {
    expect(progressOf(paymentArrangement({ paidAmount: 150, scheduledAmount: 300 }))).toBe(0.5);
    expect(progressOf(paymentArrangement({ paidAmount: 0, scheduledAmount: 0 }))).toBe(0);
  });

  it('marks an unpaid instalment overdue only once its due date has passed', () => {
    const [instalment] = arrangementInstalments();

    expect(isOverdue(instalment, '2026-10-01')).toBe(false);
    expect(isOverdue(instalment, '2026-10-02')).toBe(true);
    expect(isOverdue({ ...instalment, isSettled: true }, '2026-10-02')).toBe(false);
  });
});

describe('presentation', () => {
  it('renders an active arrangement as the successful state and a broken one as the serious one', () => {
    // The customer is paying, which is the outcome the whole feature exists to produce.
    expect(arrangementTone('Active')).toBe('success');
    expect(arrangementTone('Broken')).toBe('danger');
    expect(arrangementTone('Kept')).toBe('neutral');
    expect(arrangementTone('Proposed')).toBe('info');
  });

  it('labels every standing the host can send', () => {
    expect(arrangementLabel('Proposed')).toBe('Proposed');
    expect(arrangementLabel('Broken')).toBe('Broken');
  });

  it('offers closed accounts too, because a closed account can still hold an overdue bill', () => {
    const closed = serviceAccount({ id: 'b', accountNumber: 'A-000002', status: 'Closed' });
    const open = serviceAccount({ id: 'a', accountNumber: 'A-000001', status: 'Active' });

    expect(arrangeableAccounts([closed, open]).map((account) => account.accountNumber)).toEqual([
      'A-000001',
      'A-000002',
    ]);
  });
});
