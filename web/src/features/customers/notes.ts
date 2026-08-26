import type { CustomerNote, CustomerNoteKind, CustomerNoteLinkKind } from '@/api/customers';
import type { StatusTone } from '@/components/ui/status';

/**
 * The note log's logic, with no DOM in sight.
 *
 * The claims on that screen a rep would dispute — what order the log is in, which notes have been
 * superseded, and whether a follow-up is overdue — are worked out here and tested without rendering
 * anything. The same call `customer-360.ts`, `deposits.ts` and `contacts.ts` already made.
 *
 * Nothing here writes. The log is append-only on the host and the browser has no business pretending
 * otherwise: a correction is a POST that returns a new note, and this file's job is to make the
 * resulting collection legible.
 */

/** What each kind reads as in a pill and a filter. */
const kindLabels: Record<CustomerNoteKind, string> = {
  Note: 'Note',
  InboundCall: 'Inbound call',
  OutboundCall: 'Outbound call',
  CounterVisit: 'Counter visit',
  FieldVisit: 'Field visit',
  Complaint: 'Complaint',
  BillingDispute: 'Billing dispute',
};

/**
 * The tone each kind carries.
 *
 * A grievance and a challenge to a bill are the two that a rep opening this tab needs to see from
 * across the room, so they take the warning and danger hues. Everything else is neutral or
 * informational: a call is not an incident, and colouring it as one would spend the alarm on the
 * ordinary case.
 */
const kindTones: Record<CustomerNoteKind, StatusTone> = {
  Note: 'neutral',
  InboundCall: 'info',
  OutboundCall: 'info',
  CounterVisit: 'info',
  FieldVisit: 'info',
  Complaint: 'warning',
  BillingDispute: 'danger',
};

/** What a note's kind reads as. */
export function noteKindLabel(kind: CustomerNoteKind): string {
  return kindLabels[kind];
}

/** The pill tone a note renders with. */
export function noteKindTone(kind: CustomerNoteKind): StatusTone {
  return kindTones[kind];
}

/** What each link kind reads as beside the reference. */
const linkLabels: Record<CustomerNoteLinkKind, string> = {
  Bill: 'Bill',
  Payment: 'Payment',
  WorkOrder: 'Work order',
};

/**
 * How a note's link reads, or `null` when it has none.
 *
 * A work-order link has no reference — the host does not verify it and has no register to ask for a
 * number until WP-3.1 — so it reads as the kind alone rather than as `Work order undefined`. That is
 * the one place the temporary gap reaches the screen, and it reads as a plain fact rather than as a
 * missing value.
 */
export function noteLinkLabel(note: CustomerNote): string | null {
  if (note.linkKind === null) return null;

  const label = linkLabels[note.linkKind];

  return note.linkedReference === null ? label : `${label} ${note.linkedReference}`;
}

/**
 * The log in the order the screen shows it: pinned first, then newest first.
 *
 * **Pinned ahead of unpinned regardless of date** is WORK_PACKAGES.md's rule, and it is the whole
 * reason this is not just a date sort — the standing instruction a rep needs is usually the oldest
 * note on the account.
 *
 * The host already returns them this way, and this makes the order the screen's own rather than
 * something it inherits and cannot state. Total, and not left to sort stability: two notes recorded
 * in the same millisecond fall back to the id, which is the lesson `buildCustomerTimeline` carries.
 */
export function sortCustomerNotes(notes: readonly CustomerNote[]): CustomerNote[] {
  return notes.toSorted((left, right) => {
    if (left.isPinned !== right.isPinned) return left.isPinned ? -1 : 1;

    const byInstant = Date.parse(right.recordedAt) - Date.parse(left.recordedAt);
    if (byInstant !== 0) return byInstant;

    // Ordinal, never `localeCompare`: these are ids, not words.
    if (left.id === right.id) return 0;
    return left.id > right.id ? -1 : 1;
  });
}

/**
 * Which notes have been superseded, and by what.
 *
 * **Derived rather than stored.** The host deliberately keeps no back-pointer from a corrected note
 * to its correction: writing one would mean touching a row that is supposed to be immutable, to
 * record something a reader holding the log can work out. This is that working-out, in one place, so
 * every panel agrees about which notes are current.
 *
 * The value is the *latest* correction in a chain, not the first. A note corrected twice was wrong
 * twice, and the version a rep should be reading is the last one — while both earlier versions stay
 * in the log, which is the point of the register.
 */
export function correctionsByNote(notes: readonly CustomerNote[]): Map<string, CustomerNote> {
  const byId = new Map(notes.map((note) => [note.id, note]));
  const corrections = new Map<string, CustomerNote>();

  for (const note of notes) {
    if (note.correctsNoteId === null || !byId.has(note.correctsNoteId)) continue;

    const standing = corrections.get(note.correctsNoteId);

    // Later wins. Compared on the instant with the id as the tie-break, the same total order
    // `sortCustomerNotes` uses — two corrections written in one millisecond must not depend on which
    // order the array happened to arrive in.
    if (standing === undefined || isLaterThan(note, standing)) {
      corrections.set(note.correctsNoteId, note);
    }
  }

  return corrections;
}

function isLaterThan(candidate: CustomerNote, standing: CustomerNote): boolean {
  const byInstant = Date.parse(candidate.recordedAt) - Date.parse(standing.recordedAt);

  return byInstant !== 0 ? byInstant > 0 : candidate.id > standing.id;
}

/** The notes somebody put at the top of the log, in the log's own order. */
export function pinnedNotes(notes: readonly CustomerNote[]): CustomerNote[] {
  return sortCustomerNotes(notes.filter((note) => note.isPinned));
}

/** Where a note's follow-up stands against today. */
export type FollowUpStanding = 'none' | 'overdue' | 'today' | 'upcoming';

/**
 * Whether somebody still owes this customer a call, and whether they are late with it.
 *
 * Four states rather than two, because "due today" is the one a rep acts on before they go home and
 * "overdue" is the one that has already embarrassed the utility — showing both the same colour would
 * bury the second in the first.
 *
 * Compared as calendar days, never as instants. The host stores a `DateOnly` and the browser parses
 * it as midnight UTC; comparing that against `Date.now()` would call this morning's follow-up
 * overdue in every timezone east of Greenwich.
 */
export function followUpStanding(note: CustomerNote, today: string): FollowUpStanding {
  if (note.followUpOn === null) return 'none';

  const due = note.followUpOn.slice(0, 10);
  const day = today.slice(0, 10);

  if (due < day) return 'overdue';

  return due === day ? 'today' : 'upcoming';
}

/** What the standing reads as beside the date. */
export function followUpLabel(standing: FollowUpStanding): string | null {
  switch (standing) {
    case 'overdue':
      return 'Follow-up overdue';
    case 'today':
      return 'Follow-up today';
    case 'upcoming':
      return 'Follow-up';
    default:
      return null;
  }
}

/** The tone a follow-up renders with. */
export function followUpTone(standing: FollowUpStanding): StatusTone {
  switch (standing) {
    case 'overdue':
      return 'danger';
    case 'today':
      return 'warning';
    default:
      return 'info';
  }
}

/**
 * Today, as the host's follow-up dates are expressed.
 *
 * A `YYYY-MM-DD` string built from the browser's own calendar, so "today" means the rep's today
 * rather than UTC's. The host's floor is UTC — a few hours' disagreement either side of midnight,
 * which can only ever admit a follow-up a few hours early and never refuse one a rep meant. Sorting
 * that out properly is a timezone-per-user feature, not something to invent in a notes screen.
 */
export function todayInLocalTime(now: Date = new Date()): string {
  const year = now.getFullYear();
  const month = `${now.getMonth() + 1}`.padStart(2, '0');
  const day = `${now.getDate()}`.padStart(2, '0');

  return `${year}-${month}-${day}`;
}
