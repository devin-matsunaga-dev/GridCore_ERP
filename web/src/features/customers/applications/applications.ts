import type {
  ApplicationDocumentKind,
  ApplicationReasonCode,
  ApplicationReference,
  ServiceApplication,
  ServiceApplicationStatus,
  ServiceApplicationType,
} from '@/api/customers';
import type { StatusTone } from '@/components/ui/status';

/**
 * The review desk's logic, with no DOM in sight.
 *
 * The claims on that screen a reviewer would dispute — what is still outstanding on an application,
 * which decisions they may take on it, and which reason codes each decision offers — are worked out
 * here and tested without rendering anything. The same call `transitions.ts`, `deposits.ts` and
 * `notes.ts` already made.
 *
 * **The reason lists are NOT mirrored here, unlike WP-2.15's.** The host serves its own
 * `ApplicationReasons` map from `/api/service-application-reference`, so the select is filled from
 * the authority rather than from a copy that has to be kept in step by a pair of tests. That was the
 * one thing `transitions.ts` had to compromise on, and this package did not have to.
 */

const statusLabels: Record<ServiceApplicationStatus, string> = {
  Submitted: 'Submitted',
  UnderReview: 'Under review',
  Approved: 'Approved',
  Rejected: 'Rejected',
  Withdrawn: 'Withdrawn',
};

/**
 * The tone each status renders with.
 *
 * Submitted is waiting on somebody and Under review is somebody working on it — the distinction
 * Scheduled and In Progress already draw for a job. Withdrawn is neutral rather than the danger a
 * Rejected carries: the applicant closed it, and a queue that painted those the same colour would
 * make the desk's own refusals harder to find.
 */
const statusTones: Record<ServiceApplicationStatus, StatusTone> = {
  Submitted: 'warning',
  UnderReview: 'info',
  Approved: 'success',
  Rejected: 'danger',
  Withdrawn: 'neutral',
};

const typeLabels: Record<ServiceApplicationType, string> = {
  ResidentialConnection: 'Residential connection',
  CommercialConnection: 'Commercial connection',
};

const documentLabels: Record<ApplicationDocumentKind, string> = {
  PhotoId: 'Photo ID',
  ProofOfOccupancy: 'Lease or deed',
  BusinessLicence: 'Business licence',
  Other: 'Other document',
};

const reasonLabels: Record<ApplicationReasonCode, string> = {
  Other: 'Other (say what happened)',
  DocumentsVerified: 'Documents verified',
  ApprovedByException: 'Approved by exception (say why)',
  DocumentsIncomplete: 'Documents incomplete',
  IdentityNotVerified: 'Identity not verified',
  OccupancyNotProven: 'Occupancy not proven',
  PremiseNotServiceable: 'Premise not serviceable',
  OutstandingBalance: 'Outstanding balance',
  DuplicateApplication: 'Duplicate application',
  ApplicantWithdrew: 'Applicant withdrew',
  ApplicantUnreachable: 'Applicant unreachable',
  SupersededByAnotherApplication: 'Superseded by another application',
};

/** What an application's status reads as. Sentence case, as DESIGN.md asks. */
export function applicationStatusLabel(status: ServiceApplicationStatus): string {
  return statusLabels[status];
}

/** The pill tone an application renders with. */
export function applicationStatusTone(status: ServiceApplicationStatus): StatusTone {
  return statusTones[status];
}

/** What a kind of connection reads as. */
export function applicationTypeLabel(type: ServiceApplicationType): string {
  return typeLabels[type];
}

/** What a document reads as on the checklist — the counter's words, not the enum's. */
export function documentKindLabel(kind: ApplicationDocumentKind): string {
  return documentLabels[kind];
}

/** What a reason code reads as in a select and on a decided application. */
export function applicationReasonLabel(code: ApplicationReasonCode): string {
  return reasonLabels[code];
}

/**
 * The decisions that may be taken on an application right now, as the host reports them.
 *
 * Read off `allowedTransitions` rather than worked out from the status, so the buttons are what the
 * host's state machine actually allows — the call WP-1.5 made about every other lifecycle on the
 * site. A submitted application therefore offers a withdrawal and no decision at all, which is
 * WP-2.18's whole point: a form has to be picked up before it can be decided.
 *
 * `UnderReview` is filtered out because it is a HAND-OFF rather than a decision: it records that
 * somebody is dealing with the application, carries no reason code, and has a button of its own.
 * Offering it in the outcome select would ask a reviewer to pick "under review" as a reason for
 * something.
 */
export function availableDecisions(application: ServiceApplication): ServiceApplicationStatus[] {
  return application.allowedTransitions.filter(
    (status) => status !== 'Submitted' && status !== 'UnderReview',
  );
}

/** Whether the application is still in the queue, waiting for somebody to pick it up. */
export function isWaitingToBePickedUp(application: ServiceApplication): boolean {
  return application.status === 'Submitted';
}

/**
 * Whether approval would be refused for want of a document.
 *
 * Asked here as well as on the host, and that is not belt-and-braces: it is the difference between
 * a screen that greys the button out and says what is missing, and one that answers with a 409 after
 * the reviewer has typed a reason.
 */
export function isBlockedByChecklist(application: ServiceApplication): boolean {
  return !application.isDocumentationComplete;
}

/** What is still outstanding, in the counter's words — "Photo ID, Lease or deed". */
export function describeMissingDocuments(application: ServiceApplication): string {
  return application.missingDocuments.map(documentKindLabel).join(', ');
}

/** How far along the checklist an application is, for a queue row: `1 of 3`. */
export function checklistProgress(application: ServiceApplication): { satisfied: number; required: number } {
  return {
    satisfied: application.checklist.filter((line) => line.isSatisfied).length,
    required: application.checklist.length,
  };
}

/**
 * The reason codes a decision may be recorded under, from the host's own map.
 *
 * An unknown decision — or reference data that has not loaded yet — is an empty list rather than a
 * guess: a select offering codes the host refuses is a select whose choices produce 400s.
 */
export function reasonsForDecision(
  reference: ApplicationReference | undefined,
  decision: ServiceApplicationStatus,
): ApplicationReasonCode[] {
  return reference?.reasonCodes[decision] ?? [];
}

/**
 * Whether `code` obliges the reviewer to write something as well.
 *
 * True for `Other` and for `ApprovedByException` on the host, and read from the host rather than
 * assumed: an exception that does not say what the exception was is the one record on an
 * application that has to defend itself.
 */
export function reasonNeedsNotes(
  reference: ApplicationReference | undefined,
  code: ApplicationReasonCode | '',
): boolean {
  return code !== '' && (reference?.reasonCodesRequiringNotes ?? ['Other']).includes(code);
}

/**
 * Which document kinds may be uploaded against an application, checklist lines first.
 *
 * The order matters more than it looks: a reviewer uploading a scan reaches for the thing they are
 * still waiting on, and `Other` — which satisfies nothing — belongs at the bottom where an eye does
 * not land on it first. The same call `transitions.ts` made about `Other` in a reason list.
 */
export function uploadableKinds(
  application: ServiceApplication,
  reference: ApplicationReference | undefined,
): ApplicationDocumentKind[] {
  const required = application.checklist.map((line) => line.kind);
  const rest = (reference?.documentKinds ?? ['Other']).filter((kind) => !required.includes(kind));

  return [...required, ...rest];
}

/**
 * Whether a file may be uploaded as it stands, and what to say when it may not.
 *
 * The host refuses both of these too, and has to — a browser check is a courtesy, not a gate. What
 * it buys is that a rep at a counter is told before a ten-megabyte scan has gone over an island
 * connection, rather than after.
 */
export function rejectUpload(file: File, reference: ApplicationReference | undefined): string | null {
  const allowed = reference?.allowedContentTypes ?? [];
  const maxBytes = reference?.maxSizeInBytes;

  // An empty allow-list means the reference has not loaded; refusing everything then would be a
  // screen that breaks while it is still starting up, so the host's own check is left to it.
  if (allowed.length > 0 && !allowed.includes(file.type.split(';')[0].trim().toLowerCase())) {
    return `${file.name} is not a document type GridCore accepts. Scan it as a PDF, a JPEG or a PNG.`;
  }

  if (maxBytes !== undefined && file.size > maxBytes) {
    return `${file.name} is larger than the ${Math.round(maxBytes / (1024 * 1024))} MB limit.`;
  }

  if (file.size === 0) {
    return `${file.name} is empty; there is nothing to file.`;
  }

  return null;
}

/**
 * The register, newest first.
 *
 * The host already returns them that way — ids are Guid v7, so its key order is chronological — and
 * this makes the order the screen's own rather than something it inherits and cannot state. Total,
 * and not left to sort stability: two applications filed in the same millisecond fall back to the
 * id, which is the lesson `buildCustomerTimeline` carries.
 */
export function sortApplications(applications: readonly ServiceApplication[]): ServiceApplication[] {
  return applications.toSorted((left, right) => {
    const byInstant = Date.parse(right.submittedAt) - Date.parse(left.submittedAt);
    if (byInstant !== 0) return byInstant;

    // Ordinal, never `localeCompare`: these are ids, not words.
    if (left.id === right.id) return 0;
    return left.id > right.id ? -1 : 1;
  });
}
