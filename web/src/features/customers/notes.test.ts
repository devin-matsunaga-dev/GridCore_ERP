import { describe, expect, it } from 'vitest';
import { noteKinds, type CustomerNote } from '@/api/customers';
import { customerNote } from '@/test/registry-fixtures';
import {
  correctionsByNote,
  followUpLabel,
  followUpStanding,
  noteKindLabel,
  noteKindTone,
  noteLinkLabel,
  pinnedNotes,
  sortCustomerNotes,
  todayInLocalTime,
} from './notes';

/**
 * The note log's disputable claims, tested without a DOM: what order the log is in, which notes have
 * been superseded, and whether a follow-up is late. Milliseconds each (CONVENTIONS.md ⚡).
 */

/** A note at a given instant, with a distinct id, which is all most of these tests need. */
function at(recordedAt: string, overrides: Partial<CustomerNote> = {}): CustomerNote {
  return customerNote({ id: `0192f000-0000-7000-8000-${recordedAt.slice(8, 10)}0000000000`, recordedAt, ...overrides });
}

describe('noteKindLabel and noteKindTone', () => {
  it('has a label and a tone for every kind the host can send', () => {
    // Exhaustive rather than sampled: a kind added to the host and not here would render as
    // `undefined` in a pill, which reads as a bug in the data rather than a gap in the map.
    for (const kind of noteKinds) {
      expect(noteKindLabel(kind)).toBeTruthy();
      expect(noteKindTone(kind)).toBeTruthy();
    }
  });

  it('reserves the loud tones for the two kinds a rep needs to see from across the room', () => {
    expect(noteKindTone('Complaint')).toBe('warning');
    expect(noteKindTone('BillingDispute')).toBe('danger');

    // A call is not an incident; colouring it as one would spend the alarm on the ordinary case.
    expect(noteKindTone('InboundCall')).toBe('info');
    expect(noteKindTone('Note')).toBe('neutral');
  });
});

describe('noteLinkLabel', () => {
  it('is null when the note is about nothing in particular', () => {
    expect(noteLinkLabel(customerNote())).toBeNull();
  });

  it('reads as the register and the number', () => {
    expect(
      noteLinkLabel(customerNote({ linkKind: 'Bill', linkedEntityId: 'x', linkedReference: 'BIL-000042' })),
    ).toBe('Bill BIL-000042');

    expect(
      noteLinkLabel(customerNote({ linkKind: 'Payment', linkedEntityId: 'x', linkedReference: 'PAY-000007' })),
    ).toBe('Payment PAY-000007');
  });

  it('names a work order WITHOUT a number, because the host has none to give', () => {
    // WP-2.13's accepted gap: work orders are not verified until WP-3.1, so no reference comes back.
    // It reads as a plain fact rather than as "Work order undefined".
    expect(
      noteLinkLabel(customerNote({ linkKind: 'WorkOrder', linkedEntityId: 'x', linkedReference: null })),
    ).toBe('Work order');
  });
});

describe('sortCustomerNotes', () => {
  it('puts pinned notes ahead of unpinned REGARDLESS of date', () => {
    // WORK_PACKAGES.md's rule, and the whole reason this is not a date sort: the standing
    // instruction a rep needs is usually the oldest note on the account.
    const oldPinned = at('2026-01-01T00:30:00+00:00', { isPinned: true });
    const newUnpinned = at('2026-08-20T00:30:00+00:00');

    expect(sortCustomerNotes([newUnpinned, oldPinned]).map((note) => note.id)).toEqual([
      oldPinned.id,
      newUnpinned.id,
    ]);
  });

  it('is newest first within each group', () => {
    const older = at('2026-08-01T00:30:00+00:00');
    const newer = at('2026-08-20T00:30:00+00:00');

    expect(sortCustomerNotes([older, newer]).map((note) => note.id)).toEqual([newer.id, older.id]);
  });

  it('falls back to the id when two notes share an instant, so the order is TOTAL', () => {
    // Not left to sort stability: two notes logged in the same millisecond would otherwise reshuffle
    // between renders. The lesson `buildCustomerTimeline` carries.
    const sameInstant = '2026-08-20T00:30:00+00:00';
    const first = customerNote({ id: '0192f000-0000-7000-8000-00000000aaa1', recordedAt: sameInstant });
    const second = customerNote({ id: '0192f000-0000-7000-8000-00000000aaa2', recordedAt: sameInstant });

    expect(sortCustomerNotes([first, second]).map((note) => note.id)).toEqual([second.id, first.id]);
    expect(sortCustomerNotes([second, first]).map((note) => note.id)).toEqual([second.id, first.id]);
  });

  it('does not mutate what it was given', () => {
    const notes = [at('2026-08-01T00:30:00+00:00'), at('2026-08-20T00:30:00+00:00')];
    const order = notes.map((note) => note.id);

    sortCustomerNotes(notes);

    expect(notes.map((note) => note.id)).toEqual(order);
  });

  it('is empty for a customer nobody has written about', () => {
    expect(sortCustomerNotes([])).toEqual([]);
  });
});

describe('correctionsByNote', () => {
  const original = customerNote({ id: '0192f000-0000-7000-8000-00000000b001', body: 'No answer.' });

  it('maps a corrected note to the note that corrects it', () => {
    const correction = customerNote({
      id: '0192f000-0000-7000-8000-00000000b002',
      body: 'Answered.',
      correctsNoteId: original.id,
      recordedAt: '2026-08-21T00:30:00+00:00',
    });

    const corrections = correctionsByNote([original, correction]);

    expect(corrections.get(original.id)?.id).toBe(correction.id);

    // The correction itself has not been corrected, so it is current.
    expect(corrections.has(correction.id)).toBe(false);
  });

  it('keeps the LATEST correction in a chain, not the first', () => {
    // A note corrected twice was wrong twice, and the version a rep should read is the last one —
    // while both earlier versions stay in the log, which is the point of the register.
    const second = customerNote({
      id: '0192f000-0000-7000-8000-00000000b002',
      correctsNoteId: original.id,
      recordedAt: '2026-08-21T00:30:00+00:00',
    });
    const third = customerNote({
      id: '0192f000-0000-7000-8000-00000000b003',
      correctsNoteId: original.id,
      recordedAt: '2026-08-22T00:30:00+00:00',
    });

    expect(correctionsByNote([original, second, third]).get(original.id)?.id).toBe(third.id);

    // And the answer does not depend on which order they arrived in.
    expect(correctionsByNote([third, second, original]).get(original.id)?.id).toBe(third.id);
  });

  it('ignores a correction whose original is outside the window', () => {
    // The log is a window, so a correction of something older than it is perfectly possible. It must
    // not conjure a map entry for a note nothing on screen can show.
    const orphan = customerNote({
      id: '0192f000-0000-7000-8000-00000000b009',
      correctsNoteId: '0192f000-0000-7000-8000-00000000bfff',
    });

    expect(correctionsByNote([orphan]).size).toBe(0);
  });

  it('is empty when nothing has been corrected', () => {
    expect(correctionsByNote([original]).size).toBe(0);
  });
});

describe('pinnedNotes', () => {
  it('is the pinned ones, in the log’s own order', () => {
    const older = at('2026-01-01T00:30:00+00:00', { isPinned: true });
    const newer = at('2026-08-20T00:30:00+00:00', { isPinned: true });
    const unpinned = at('2026-09-01T00:30:00+00:00');

    expect(pinnedNotes([older, unpinned, newer]).map((note) => note.id)).toEqual([newer.id, older.id]);
  });

  it('is empty when nothing is pinned, which is what keeps the strip off the summary', () => {
    expect(pinnedNotes([customerNote()])).toEqual([]);
  });
});

describe('followUpStanding', () => {
  const today = '2026-08-26';

  it('is none when nobody set one', () => {
    expect(followUpStanding(customerNote({ followUpOn: null }), today)).toBe('none');
    expect(followUpLabel('none')).toBeNull();
  });

  it('tells overdue, today and upcoming apart', () => {
    // Three states rather than two: "due today" is the one a rep acts on before they go home, and
    // "overdue" is the one that has already embarrassed the utility.
    expect(followUpStanding(customerNote({ followUpOn: '2026-08-25' }), today)).toBe('overdue');
    expect(followUpStanding(customerNote({ followUpOn: today }), today)).toBe('today');
    expect(followUpStanding(customerNote({ followUpOn: '2026-08-27' }), today)).toBe('upcoming');
  });

  it('compares CALENDAR DAYS, not instants', () => {
    // The host stores a DateOnly and the browser parses it as midnight UTC. Comparing that against a
    // clock would call this morning's follow-up overdue in every timezone east of Greenwich.
    expect(followUpStanding(customerNote({ followUpOn: '2026-08-26T00:00:00.000Z' }), '2026-08-26T23:59:00+00:00'))
      .toBe('today');
  });
});

describe('todayInLocalTime', () => {
  it('is the browser’s own calendar day, zero-padded', () => {
    // Built from the local calendar rather than from an ISO string, so "today" means the rep's today
    // rather than UTC's — which is what the form's date floor has to be.
    expect(todayInLocalTime(new Date(2026, 0, 5, 23, 30))).toBe('2026-01-05');
    expect(todayInLocalTime(new Date(2026, 11, 31, 0, 30))).toBe('2026-12-31');
  });
});
