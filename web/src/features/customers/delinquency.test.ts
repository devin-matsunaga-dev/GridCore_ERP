import { describe, expect, it } from 'vitest';
import {
  delinquencyAccounts,
  eligibilitySummary,
  eligibilityTone,
  hasOffsetToApply,
  nextNoticeToServe,
  noticeLabel,
  noticeTone,
  occupiedBuckets,
  sortNotices,
} from './delinquency';
import {
  accountArrears,
  delinquency,
  disconnectionEligibility,
  dunningNotice,
  serviceAccount,
} from '@/test/registry-fixtures';

/**
 * The delinquency tab's logic, with no DOM in sight (WP-2.19).
 *
 * What is tested here is presentation. **No rule is re-implemented in the browser**, so there is
 * nothing here asserting whether an account may be disconnected — that is decided by the host, and a
 * second opinion about whether somebody's electricity gets cut off is the last thing a screen should
 * hold.
 */
describe('notice labels and tones', () => {
  it('escalates in tone as the sequence escalates in seriousness', () => {
    expect(noticeTone('Reminder')).toBe('info');
    expect(noticeTone('Delinquency')).toBe('warning');
    expect(noticeTone('Disconnection')).toBe('danger');
  });

  it('reads each notice by the name the utility gives it', () => {
    expect(noticeLabel('Disconnection')).toBe('Notice of disconnection');
  });
});

describe('occupiedBuckets', () => {
  it('drops the empty bands, because five zeroes say less than one figure', () => {
    const bands = occupiedBuckets(accountArrears());

    expect(bands.map((bucket) => bucket.label)).toEqual(['Not yet due', '61-90 days']);
  });

  it('keeps the host order, because an ageing read out of order is a different report', () => {
    const arrears = accountArrears({
      buckets: [
        { label: 'Not yet due', fromDays: 0, toDays: 0, amount: 10 },
        { label: '1-30 days', fromDays: 1, toDays: 30, amount: 20 },
        { label: '31-60 days', fromDays: 31, toDays: 60, amount: 30 },
        { label: '61-90 days', fromDays: 61, toDays: 90, amount: 40 },
        { label: 'Over 90 days', fromDays: 91, toDays: null, amount: 50 },
      ],
    });

    expect(occupiedBuckets(arrears).map((bucket) => bucket.amount)).toEqual([10, 20, 30, 40, 50]);
  });
});

describe('sortNotices', () => {
  it('reads newest first, by the day served rather than the day recorded', () => {
    const older = dunningNotice({ id: 'a', servedOn: '2026-07-01', noticeType: 'Reminder' });
    const newer = dunningNotice({ id: 'b', servedOn: '2026-08-10' });

    expect(sortNotices([older, newer]).map((notice) => notice.id)).toEqual(['b', 'a']);
  });

  it('falls back to the id, so two notices served on one day have a total order', () => {
    const first = dunningNotice({ id: 'a', servedOn: '2026-08-10' });
    const second = dunningNotice({ id: 'b', servedOn: '2026-08-10' });

    expect(sortNotices([first, second]).map((notice) => notice.id)).toEqual(['b', 'a']);
  });
});

describe('nextNoticeToServe', () => {
  it('offers the step the account has reached when nothing has been served', () => {
    expect(nextNoticeToServe(delinquency())!.noticeType).toBe('Disconnection');
  });

  it('offers nothing once that step has been served', () => {
    // Re-serving a notice because the debt grew would restart a statutory clock the customer is
    // already inside.
    const picture = delinquency({ notices: [dunningNotice({ noticeType: 'Disconnection' })] });

    expect(nextNoticeToServe(picture)).toBeUndefined();
  });

  it('offers nothing where the account has reached no step at all', () => {
    expect(nextNoticeToServe(delinquency({ dueStep: null }))).toBeUndefined();
  });
});

describe('eligibilitySummary', () => {
  it('calls the deposit case out by name, because it is the one the statute exists for', () => {
    const summary = eligibilitySummary(
      disconnectionEligibility({
        depositHeldBeforeOffset: 300,
        offsetAmount: 200,
        arrearsAfterOffset: 0,
        depositClearsArrears: true,
      }),
    );

    expect(summary).toMatch(/deposit clears the arrears/);
    expect(summary).toMatch(/not eligible/);
  });

  it('says so plainly when every test is satisfied', () => {
    const summary = eligibilitySummary(disconnectionEligibility({ isEligible: true, blockers: [] }));

    expect(summary).toMatch(/eligible for disconnection/);
  });

  it('names the single outstanding test where there is only one', () => {
    const summary = eligibilitySummary(
      disconnectionEligibility({ blockers: ['Statutory waiting period elapsed'] }),
    );

    expect(summary).toMatch(/statutory waiting period elapsed is outstanding/);
  });

  it('counts them where there are several', () => {
    expect(eligibilitySummary(disconnectionEligibility())).toMatch(/2 of the four tests/);
  });
});

describe('eligibilityTone', () => {
  it('renders eligible as the serious answer rather than the successful one', () => {
    // A customer about to lose their supply is not a success state, whatever the workflow thinks.
    expect(eligibilityTone(disconnectionEligibility({ isEligible: true, blockers: [] }))).toBe('danger');
    expect(eligibilityTone(disconnectionEligibility())).toBe('neutral');
  });
});

describe('hasOffsetToApply', () => {
  it('is false where there is no deposit to move', () => {
    expect(hasOffsetToApply(disconnectionEligibility())).toBe(false);
  });

  it('is true where a deposit qualifies and has not been applied', () => {
    expect(hasOffsetToApply(disconnectionEligibility({ offsetAmount: 200 }))).toBe(true);
  });

  it('is false once it has been applied, so the button stops promising to do it again', () => {
    expect(hasOffsetToApply(disconnectionEligibility({ offsetAmount: 200, isOffsetApplied: true }))).toBe(false);
  });
});

describe('delinquencyAccounts', () => {
  it('keeps closed accounts, because a closed account can still hold an overdue bill', () => {
    const open = serviceAccount({ id: 'b', accountNumber: 'A-000002', status: 'Active' });
    const closed = serviceAccount({ id: 'a', accountNumber: 'A-000001', status: 'Closed' });

    expect(delinquencyAccounts([open, closed]).map((account) => account.id)).toEqual(['a', 'b']);
  });
});
