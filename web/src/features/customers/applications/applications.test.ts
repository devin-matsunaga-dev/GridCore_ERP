import { describe, expect, it } from 'vitest';
import { applicationReference, serviceApplication } from '@/test/registry-fixtures';
import {
  applicationReasonLabel,
  applicationStatusLabel,
  applicationStatusTone,
  applicationTypeLabel,
  availableDecisions,
  checklistProgress,
  describeMissingDocuments,
  documentKindLabel,
  isBlockedByChecklist,
  isWaitingToBePickedUp,
  reasonNeedsNotes,
  reasonsForDecision,
  rejectUpload,
  sortApplications,
  uploadableKinds,
} from './applications';

/** A file of a given type and size, without touching a disk. */
function file(name: string, type: string, bytes = 1_024): File {
  return new File([new Uint8Array(bytes)], name, { type });
}

const reference = applicationReference();

describe('availableDecisions', () => {
  it('offers no decision on an application nobody has picked up', () => {
    // WP-2.18's whole point, as a screen: a form has to be read before it is decided, so a submitted
    // application offers a withdrawal and nothing else.
    expect(availableDecisions(serviceApplication())).toEqual(['Withdrawn']);
  });

  it('offers all three once it is under review', () => {
    const application = serviceApplication({
      status: 'UnderReview',
      allowedTransitions: ['Approved', 'Rejected', 'Withdrawn'],
    });

    expect(availableDecisions(application)).toEqual(['Approved', 'Rejected', 'Withdrawn']);
  });

  it('offers nothing at all once it has been decided', () => {
    const application = serviceApplication({ status: 'Rejected', allowedTransitions: [], isOpen: false });

    expect(availableDecisions(application)).toEqual([]);
  });

  it('reads the host rather than inferring from the status', () => {
    // The host is the authority on its own state machine. A screen that worked the buttons out from
    // the status would be a second copy of it, and the two would part company the first time the
    // machine changed.
    const application = serviceApplication({ status: 'Submitted', allowedTransitions: ['Approved'] });

    expect(availableDecisions(application)).toEqual(['Approved']);
  });
});

describe('the checklist', () => {
  it('counts what has arrived against what is required', () => {
    expect(checklistProgress(serviceApplication())).toEqual({ satisfied: 0, required: 2 });

    const half = serviceApplication({
      checklist: [
        { kind: 'PhotoId', isSatisfied: true, documentId: 'doc-1', uploadedAt: '2026-08-27T09:40:00+00:00' },
        { kind: 'ProofOfOccupancy', isSatisfied: false, documentId: null, uploadedAt: null },
      ],
    });

    expect(checklistProgress(half)).toEqual({ satisfied: 1, required: 2 });
  });

  it('blocks approval while anything is outstanding, and says what in the counter’s words', () => {
    const application = serviceApplication();

    expect(isBlockedByChecklist(application)).toBe(true);
    expect(describeMissingDocuments(application)).toBe('Photo ID, Lease or deed');
  });

  it('stops blocking once the host says the documentation is complete', () => {
    const complete = serviceApplication({ isDocumentationComplete: true, missingDocuments: [] });

    expect(isBlockedByChecklist(complete)).toBe(false);
    expect(describeMissingDocuments(complete)).toBe('');
  });
});

describe('uploadableKinds', () => {
  it('puts the checklist first and the escape hatch last', () => {
    // A reviewer reaches for what they are still waiting on; `Other` satisfies nothing, so an eye
    // must not land on it first.
    expect(uploadableKinds(serviceApplication(), reference)).toEqual([
      'PhotoId',
      'ProofOfOccupancy',
      'BusinessLicence',
      'Other',
    ]);
  });

  it('still offers something before the reference data has loaded', () => {
    expect(uploadableKinds(serviceApplication(), undefined)).toEqual(['PhotoId', 'ProofOfOccupancy', 'Other']);
  });
});

describe('reasonsForDecision', () => {
  it('reads the host’s own map rather than a copy of it', () => {
    // The one thing WP-2.15's transitions had to compromise on. The host serves its reason lists, so
    // there is nothing here to fall out of step with them.
    expect(reasonsForDecision(reference, 'Approved')).toEqual(['DocumentsVerified', 'ApprovedByException', 'Other']);
    expect(reasonsForDecision(reference, 'Withdrawn')).toContain('ApplicantWithdrew');
  });

  it('offers nothing for a decision the host does not list, and nothing before it has loaded', () => {
    expect(reasonsForDecision(reference, 'Submitted')).toEqual([]);
    expect(reasonsForDecision(undefined, 'Approved')).toEqual([]);
  });
});

describe('reasonNeedsNotes', () => {
  it('demands a sentence for the two codes that escape the list', () => {
    expect(reasonNeedsNotes(reference, 'Other')).toBe(true);
    expect(reasonNeedsNotes(reference, 'ApprovedByException')).toBe(true);
    expect(reasonNeedsNotes(reference, 'DocumentsVerified')).toBe(false);
  });

  it('demands nothing while no code has been chosen', () => {
    expect(reasonNeedsNotes(reference, '')).toBe(false);
  });

  it('falls back to Other alone before the reference has loaded', () => {
    expect(reasonNeedsNotes(undefined, 'Other')).toBe(true);
    expect(reasonNeedsNotes(undefined, 'ApprovedByException')).toBe(false);
  });
});

describe('rejectUpload', () => {
  it('accepts what the host accepts', () => {
    expect(rejectUpload(file('lease.pdf', 'application/pdf'), reference)).toBeNull();
    expect(rejectUpload(file('id.jpg', 'image/jpeg'), reference)).toBeNull();
  });

  it('refuses a type the host would refuse, before the bytes leave', () => {
    expect(rejectUpload(file('macro.docx', 'application/msword'), reference)).toMatch(/not a document type/);
  });

  it('ignores the parameters a browser appends to a media type', () => {
    // Some browsers send "image/jpeg; charset=binary". Refusing a well-formed header for carrying
    // something the allow-list does not care about would be a bug at the counter.
    expect(rejectUpload(file('id.jpg', 'image/jpeg; charset=binary'), reference)).toBeNull();
  });

  it('refuses a file past the published limit, and an empty one', () => {
    expect(rejectUpload(file('huge.pdf', 'application/pdf', 11 * 1024 * 1024), reference)).toMatch(/larger than/);
    expect(rejectUpload(file('empty.pdf', 'application/pdf', 0), reference)).toMatch(/empty/);
  });

  it('lets the host decide while the reference has not loaded', () => {
    // Refusing everything at startup would be a screen that breaks while it is still coming up.
    expect(rejectUpload(file('lease.pdf', 'application/pdf'), undefined)).toBeNull();
  });
});

describe('sortApplications', () => {
  it('reads newest first', () => {
    const older = serviceApplication({ id: 'a', applicationNumber: 'AP-000001', submittedAt: '2026-08-25T09:00:00+00:00' });
    const newer = serviceApplication({ id: 'b', applicationNumber: 'AP-000002', submittedAt: '2026-08-27T09:00:00+00:00' });

    expect(sortApplications([older, newer]).map((row) => row.applicationNumber)).toEqual(['AP-000002', 'AP-000001']);
  });

  it('is total, so two filed in the same millisecond do not depend on sort stability', () => {
    const first = serviceApplication({ id: 'aaa', applicationNumber: 'AP-000001' });
    const second = serviceApplication({ id: 'bbb', applicationNumber: 'AP-000002' });

    expect(sortApplications([first, second]).map((row) => row.id)).toEqual(['bbb', 'aaa']);
    expect(sortApplications([second, first]).map((row) => row.id)).toEqual(['bbb', 'aaa']);
  });
});

describe('labels', () => {
  it('reads statuses, types, documents and reasons in sentence case', () => {
    expect(applicationStatusLabel('UnderReview')).toBe('Under review');
    expect(applicationTypeLabel('CommercialConnection')).toBe('Commercial connection');
    expect(documentKindLabel('ProofOfOccupancy')).toBe('Lease or deed');
    expect(applicationReasonLabel('DocumentsIncomplete')).toBe('Documents incomplete');
  });

  it('paints a withdrawal neutral and a rejection as the danger it is', () => {
    // A queue that painted the applicant's own closure the same colour as the utility's refusal
    // would make the desk's own decisions harder to find.
    expect(applicationStatusTone('Withdrawn')).toBe('neutral');
    expect(applicationStatusTone('Rejected')).toBe('danger');
    expect(applicationStatusTone('Approved')).toBe('success');
  });
});

describe('isWaitingToBePickedUp', () => {
  it('is true only while the application is still in the queue', () => {
    expect(isWaitingToBePickedUp(serviceApplication())).toBe(true);
    expect(isWaitingToBePickedUp(serviceApplication({ status: 'UnderReview' }))).toBe(false);
  });
});
