import { describe, expect, it } from 'vitest';
import { accountTransitionKinds, transitionReasonCodes, type AccountTransition } from '@/api/customers';
import { accountTransition, serviceAccount } from '@/test/registry-fixtures';
import {
  describeTransition,
  isDated,
  movableAccounts,
  otherClass,
  sortTransitions,
  transitionKindLabel,
  transitionNeedsNotes,
  transitionReasonLabel,
  transitionReasonsFor,
} from './transitions';

describe('the reason map', () => {
  it('gives every kind a non-empty list', () => {
    // A kind with no reasons would render an empty select, which is a form that cannot be submitted
    // and gives no clue why.
    for (const kind of accountTransitionKinds) {
      expect(transitionReasonsFor(kind).length).toBeGreaterThan(0);
    }
  });

  it('offers the escape hatch on every kind, last', () => {
    // Last rather than first: `Other` is what a rep reaches for when none of the others fits, not
    // the option their eye lands on.
    for (const kind of accountTransitionKinds) {
      const reasons = transitionReasonsFor(kind);

      expect(reasons.at(-1)).toBe('Other');
    }
  });

  it('never offers a code the label map has no words for', () => {
    // A select option rendering `undefined` is the failure this catches, and it is the one that
    // happens the day somebody adds a code to the host and mirrors half of it.
    for (const kind of accountTransitionKinds) {
      for (const code of transitionReasonsFor(kind)) {
        expect(transitionReasonLabel(code)).toBeTruthy();
      }
    }
  });

  it('labels every code the host declares', () => {
    for (const code of transitionReasonCodes) {
      expect(transitionReasonLabel(code)).toBeTruthy();
    }
  });

  it('does not offer a customer request as a reason to re-classify', () => {
    // The mirror of the host's rule: a class is what the premise is used for, not what its occupant
    // would prefer to be billed as.
    expect(transitionReasonsFor('ClassChanged')).not.toContain('CustomerRequest');
  });

  it('does not offer a transfer a reason that ends a supply for good', () => {
    // Offering these would let a rep record a customer as having left while opening them an account
    // somewhere else — two claims that cannot both be true.
    const transfer = transitionReasonsFor('Transferred');

    expect(transfer).not.toContain('PropertyDemolished');
    expect(transfer).not.toContain('Deceased');
    expect(transfer).not.toContain('EndOfTenancy');
  });

  it('asks for notes on the escape hatch and on nothing else', () => {
    expect(transitionReasonCodes.filter(transitionNeedsNotes)).toEqual(['Other']);
  });
});

describe('describeTransition', () => {
  it('reads a class or status move as an arrow between the two sides', () => {
    expect(
      describeTransition(accountTransition({ kind: 'ClassChanged', fromValue: 'Residential', toValue: 'Commercial' })),
    ).toBe('Residential → Commercial');
  });

  it('reads a move-in as having no before and a move-out as having no after', () => {
    // Which is what the two acts ARE. Rendering "null → A-000002" would be the table showing a
    // reader a value the register deliberately does not hold.
    expect(
      describeTransition(accountTransition({ kind: 'MovedIn', fromValue: null, toValue: 'A-000002' })),
    ).toBe('Service opened on A-000002');

    expect(
      describeTransition(accountTransition({ kind: 'MovedOut', fromValue: 'A-000001', toValue: null })),
    ).toBe('Service closed on A-000001');
  });

  it('reads a transfer as one account giving way to the other', () => {
    expect(
      describeTransition(accountTransition({ kind: 'Transferred', fromValue: 'A-000001', toValue: 'A-000002' })),
    ).toBe('A-000001 → A-000002');
  });

  it('never renders an empty cell for any kind the host declares', () => {
    // Failure path for the table rather than for the register: a row with nothing in its Change
    // column reads as a row that failed to load.
    for (const kind of accountTransitionKinds) {
      expect(describeTransition(accountTransition({ kind, fromValue: null, toValue: null }))).toBeTruthy();
    }
  });
});

describe('isDated', () => {
  it('is false when the change applies on the day it was recorded', () => {
    expect(
      isDated(accountTransition({ recordedAt: '2026-08-26T10:15:00+00:00', effectiveOn: '2026-08-26' })),
    ).toBe(false);
  });

  it('is true for a back-dated change and for a forward-dated one', () => {
    // The mark a back-dated re-classification would otherwise hide behind: the row says "today" in
    // the recorded column and prices from another month.
    expect(
      isDated(accountTransition({ recordedAt: '2026-08-26T10:15:00+00:00', effectiveOn: '2026-07-01' })),
    ).toBe(true);

    expect(
      isDated(accountTransition({ recordedAt: '2026-08-26T10:15:00+00:00', effectiveOn: '2026-09-01' })),
    ).toBe(true);
  });
});

describe('movableAccounts', () => {
  it('offers only the accounts still holding a premise', () => {
    // A closed account has already been moved out of, and offering it would be a select whose
    // choices produce 409s.
    const open = serviceAccount({ id: 'a1', accountNumber: 'A-000002', status: 'Active' });
    const pending = serviceAccount({ id: 'a2', accountNumber: 'A-000001', status: 'Pending' });
    const closed = serviceAccount({ id: 'a3', accountNumber: 'A-000003', status: 'Closed' });

    expect(movableAccounts([open, pending, closed]).map((account) => account.accountNumber)).toEqual([
      'A-000001',
      'A-000002',
    ]);
  });

  it('offers nothing for a customer with no open account', () => {
    expect(movableAccounts([serviceAccount({ status: 'Closed' })])).toEqual([]);
  });
});

describe('otherClass', () => {
  it('is the one the customer is not on', () => {
    // There are two, so a class change is a button rather than a select — and the host refuses a
    // move to the class already held with a 409.
    expect(otherClass('Residential')).toBe('Commercial');
    expect(otherClass('Commercial')).toBe('Residential');
  });
});

describe('sortTransitions', () => {
  it('is newest RECORDED first, not newest effective', () => {
    // The register is a record of what was done and when. Ordering by effective date would put a
    // change dated next month above one made this morning, and a rep reading down it could not tell
    // what happened last.
    const yesterday = accountTransition({
      id: 'b',
      recordedAt: '2026-08-25T09:00:00+00:00',
      effectiveOn: '2026-12-01',
    });

    const today = accountTransition({
      id: 'a',
      recordedAt: '2026-08-26T09:00:00+00:00',
      effectiveOn: '2026-01-01',
    });

    expect(sortTransitions([yesterday, today]).map((transition) => transition.id)).toEqual(['a', 'b']);
  });

  it('falls back to the id so two recorded in the same millisecond do not reshuffle', () => {
    const first: AccountTransition = accountTransition({ id: 'aaa', recordedAt: '2026-08-26T09:00:00+00:00' });
    const second: AccountTransition = accountTransition({ id: 'bbb', recordedAt: '2026-08-26T09:00:00+00:00' });

    expect(sortTransitions([first, second]).map((transition) => transition.id)).toEqual(['bbb', 'aaa']);
    expect(sortTransitions([second, first]).map((transition) => transition.id)).toEqual(['bbb', 'aaa']);
  });

  it('does not mutate what it was given', () => {
    const rows = [accountTransition({ id: 'a' }), accountTransition({ id: 'b' })];

    sortTransitions(rows);

    expect(rows.map((transition) => transition.id)).toEqual(['a', 'b']);
  });
});

describe('transitionKindLabel', () => {
  it('has words for every kind the host declares', () => {
    for (const kind of accountTransitionKinds) {
      expect(transitionKindLabel(kind)).toBeTruthy();
    }
  });
});
