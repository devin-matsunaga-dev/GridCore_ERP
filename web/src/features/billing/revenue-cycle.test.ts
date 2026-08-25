import { describe, expect, it } from 'vitest';
import {
  bill,
  billTotal,
  journalEntry,
  meterReading,
  paidBill,
  readingCycle,
  receivables,
  takePaymentResult,
  trialBalance,
} from '@/test/revenue-cycle-fixtures';
import { customer, meter, serviceAccount, serviceLocation } from '@/test/registry-fixtures';
import {
  activeStepId,
  completedStepCount,
  centsEqual,
  isCycleComplete,
  isStepComplete,
  nextCycleCode,
  reconcile,
  revenueCycleStepIds,
  revenueCycleSteps,
  stepStatus,
  type RevenueCycleState,
} from './revenue-cycle';

/** The state after every step has run — each test peels back from here. */
function completed(): RevenueCycleState {
  return {
    customer: customer(),
    location: serviceLocation(),
    account: serviceAccount(),
    meter: meter(),
    cycle: readingCycle(),
    reading: meterReading(),
    bill: bill(),
    payment: takePaymentResult(),
    settledBill: paidBill(),
    postedEntries: [journalEntry()],
  };
}

describe('the steps', () => {
  it('covers every one of SPEC.md’s nine revenue-cycle steps exactly once', () => {
    // The demonstration is the MVP's acceptance target, so a step quietly dropped from the walk
    // would be a hole in what the product claims to prove.
    const covered = revenueCycleSteps.flatMap((step) => step.specSteps);

    expect(covered).toEqual([
      'Create Customer',
      'Create Service Account',
      'Assign Meter',
      'Generate Simulated Reading',
      'Calculate Consumption',
      'Generate Bill',
      'Run Simulated Payment',
      'Update Balance',
      'Generate Accounting Entries',
    ]);
  });

  it('declares the steps in the order the walk runs them', () => {
    expect(revenueCycleSteps.map((step) => step.id)).toEqual([...revenueCycleStepIds]);
  });
});

describe('step completion', () => {
  it('holds the customer step open until the premise exists as well as the customer', () => {
    // Two registries, two records: a customer with nowhere to be served has not finished the step.
    expect(isStepComplete({ customer: customer() }, 'customer')).toBe(false);
    expect(isStepComplete({ customer: customer(), location: serviceLocation() }, 'customer')).toBe(true);
  });

  it('does not count an account that was opened but never energised', () => {
    // The billing run refuses one: nothing was supplied, so the units on the meter are not its
    // units to be charged for. A walk that ticked this step would fail four steps later.
    const pending = serviceAccount({ status: 'Pending', serviceStartedAt: null });

    expect(isStepComplete({ account: pending }, 'service-account')).toBe(false);
    expect(isStepComplete({ account: serviceAccount() }, 'service-account')).toBe(true);
  });

  it('does not count a meter that is registered but not yet fitted', () => {
    const inStore = meter({ status: 'InStore', isFitted: false, serviceLocationId: null, serviceLocation: null });

    expect(isStepComplete({ meter: inStore }, 'meter')).toBe(false);
    expect(isStepComplete({ meter: meter() }, 'meter')).toBe(true);
  });

  it('holds the books open until Finance has actually posted', () => {
    // Re-reading the bill is not the accounting step: Finance posts from an event, asynchronously,
    // and a walk that ticked this step off the moment a payment returned would be claiming a ledger
    // it had not looked at.
    const { postedEntries, ...beforeThePostings } = completed();

    expect(postedEntries).toHaveLength(1);
    expect(isStepComplete(beforeThePostings, 'accounting')).toBe(false);
    expect(activeStepId(beforeThePostings)).toBe('accounting');
  });

  it('counts the payment step as done when the provider refused', () => {
    // A refusal is an answer. The walk carries on to the books either way — and what the books then
    // say is that the customer still owes the money, which is the point of showing it.
    const refused = takePaymentResult({ approved: false });

    expect(isStepComplete({ payment: refused }, 'payment')).toBe(true);
  });
});

describe('the active step', () => {
  it('starts on the customer', () => {
    expect(activeStepId({})).toBe('customer');
    expect(stepStatus({}, 'customer')).toBe('active');
    expect(stepStatus({}, 'accounting')).toBe('waiting');
  });

  it('moves on as each step produces what it exists to produce', () => {
    const state: RevenueCycleState = { customer: customer(), location: serviceLocation() };

    expect(activeStepId(state)).toBe('service-account');
    expect(stepStatus(state, 'customer')).toBe('done');
    expect(completedStepCount(state)).toBe(1);
  });

  it('is null once the whole cycle has run', () => {
    expect(activeStepId(completed())).toBeNull();
    expect(isCycleComplete(completed())).toBe(true);
    expect(completedStepCount(completed())).toBe(revenueCycleStepIds.length);
  });

  /**
   * Failure path. State arriving out of order should point at the earliest gap, not at the step
   * after the last thing that happened — that is the one somebody can actually fill.
   */
  it('points at the earliest gap when a later step somehow ran first', () => {
    const state: RevenueCycleState = { customer: customer(), location: serviceLocation(), bill: bill() };

    expect(activeStepId(state)).toBe('service-account');
    expect(stepStatus(state, 'bill')).toBe('done');
    expect(stepStatus(state, 'meter')).toBe('waiting');
  });
});

describe('the cycle code', () => {
  it('is stamped to the minute so a second demonstration does not collide with the first', () => {
    // The code is the idempotency key behind ux_meter_readings_meter_cycle: reusing one is a 409.
    expect(nextCycleCode(new Date(2026, 7, 25, 9, 30))).toBe('DEMO-20260825-0930');
  });

  it('pads every part, and fits the column', () => {
    const code = nextCycleCode(new Date(2026, 0, 2, 3, 4));

    expect(code).toBe('DEMO-20260102-0304');
    // MeterReading.CycleCodeLength / Bill.CycleCodeLength.
    expect(code.length).toBeLessThanOrEqual(32);
  });
});

describe('reconciliation', () => {
  it('agrees when Billing and the ledger arrived at the same figures independently', () => {
    const result = reconcile(paidBill(), receivables(), trialBalance());

    expect(result.reconciles).toBe(true);
    expect(result.ledgerBalances).toBe(true);
    expect(result.checks.every((check) => check.agrees)).toBe(true);
    expect(result.checks.map((check) => check.id)).toEqual(['charged', 'settled', 'outstanding']);
  });

  it('reconciles an unpaid bill too — a debt is as balanced an entry as a settlement', () => {
    const owed = receivables({
      rows: [{ ...receivables().rows[0]!, settled: 0, outstanding: billTotal, postingCount: 1 }],
      totalSettled: 0,
      totalOutstanding: billTotal,
    });

    const result = reconcile(bill(), owed, trialBalance());

    expect(result.reconciles).toBe(true);
    expect(result.checks.find((check) => check.id === 'outstanding')?.finance).toBe(billTotal);
  });

  /** Failure path: the books disagreeing with the bill is the one thing this screen must not hide. */
  it('refuses to reconcile when the ledger settled money the bill has not been paid', () => {
    const wrong = receivables({
      rows: [{ ...receivables().rows[0]!, settled: billTotal, outstanding: 0 }],
    });

    const result = reconcile(bill(), wrong, trialBalance());

    expect(result.reconciles).toBe(false);
    expect(result.checks.find((check) => check.id === 'settled')?.agrees).toBe(false);
    // The charge still agrees — a partial disagreement must be reported as one, not as a blanket
    // failure that gives a reader nowhere to look.
    expect(result.checks.find((check) => check.id === 'charged')?.agrees).toBe(true);
  });

  /** Failure path: invariant 3 breaking anywhere in the ledger fails the whole reconciliation. */
  it('refuses to reconcile when the trial balance does not balance', () => {
    const result = reconcile(paidBill(), receivables(), trialBalance({ isBalanced: false, difference: 0.01 }));

    expect(result.ledgerBalances).toBe(false);
    expect(result.reconciles).toBe(false);
    // …even though every figure about this one account still agrees, which is exactly the case a
    // per-account check on its own would wave through.
    expect(result.checks.every((check) => check.agrees)).toBe(true);
  });

  it('reads a ledger with no receivables line for the account as nothing charged', () => {
    // What a freshly seeded demo world answers: WP-2.6 shipped no seeder, so the ledger is empty
    // until the walk fills it. Nothing charged is a real answer; it just does not match a bill.
    const empty = receivables({ rows: [], totalCharged: 0, totalSettled: 0, totalOutstanding: 0 });

    const result = reconcile(bill(), empty, trialBalance());

    expect(result.checks.find((check) => check.id === 'charged')?.finance).toBe(0);
    expect(result.reconciles).toBe(false);
  });
});

describe('money comparison', () => {
  it('compares to the cent rather than by identity', () => {
    // 0.1 + 0.2 is a poor reason to tell somebody their ledger is wrong.
    expect(centsEqual(0.1 + 0.2, 0.3)).toBe(true);
    expect(centsEqual(63.62, 63.62)).toBe(true);
  });

  it('still notices a cent', () => {
    expect(centsEqual(63.62, 63.63)).toBe(false);
  });
});
