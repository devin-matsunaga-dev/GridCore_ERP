import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Check, FileText, Paperclip, Upload } from 'lucide-react';
import { useMemo, useRef, useState } from 'react';
import { Link } from 'react-router';
import {
  applicationKeys,
  applicationsApi,
  customerKeys,
  serviceTypeLabel,
  useApplicationReference,
  type ApplicationApproval,
  type ApplicationDocumentKind,
  type ApplicationReasonCode,
  type ServiceApplication,
  type ServiceApplicationStatus,
} from '@/api/customers';
import { toast } from '@/components/feedback/toast';
import { DetailList, orNotRecorded } from '@/components/registry/detail-list';
import { Drawer } from '@/components/registry/drawer';
import { Button } from '@/components/ui/button';
import { Label } from '@/components/ui/label';
import { Select } from '@/components/ui/select';
import { StatusPill } from '@/components/ui/status';
import { Textarea } from '@/components/ui/textarea';
import { formatDate, formatDateTime } from '@/lib/format';
import {
  applicationReasonLabel,
  applicationStatusLabel,
  applicationStatusTone,
  applicationTypeLabel,
  availableDecisions,
  describeMissingDocuments,
  documentKindLabel,
  isBlockedByChecklist,
  isWaitingToBePickedUp,
  reasonNeedsNotes,
  reasonsForDecision,
  rejectUpload,
  uploadableKinds,
} from './applications';

/**
 * One application, as a reviewer works it: what was applied for, what the checklist still wants,
 * the evidence that has arrived, and the decision.
 *
 * **A drawer rather than a page**, the call the premise and service-account registries already
 * made: a reviewer works down a queue, and losing the list, its filters and its scroll position
 * between applications is what makes a queue tiring to work.
 *
 * **The buttons come from `allowedTransitions`**, so what the screen offers is what the host's
 * state machine allows — a submitted application offers a withdrawal and no decision at all, which
 * is WP-2.18's whole point rather than an omission.
 */
export function ApplicationReviewDrawer({
  application,
  onClose,
}: {
  application: ServiceApplication | null;
  onClose: () => void;
}) {
  return (
    <Drawer
      open={application !== null}
      onClose={onClose}
      title={application?.applicationNumber ?? ''}
      subtitle={
        application && (
          <span className="flex flex-wrap items-center gap-2">
            <StatusPill
              status={applicationStatusLabel(application.status)}
              tone={applicationStatusTone(application.status)}
            />
            <span className="text-muted text-[13px]">{applicationTypeLabel(application.type)}</span>
          </span>
        )
      }
    >
      {application && <ApplicationReviewBody application={application} onDecided={onClose} />}
    </Drawer>
  );
}

function ApplicationReviewBody({
  application,
  onDecided,
}: {
  application: ServiceApplication;
  onDecided: () => void;
}) {
  const reference = useApplicationReference();
  const queryClient = useQueryClient();

  /**
   * Everything an application touches, invalidated together.
   *
   * An approval is not only an application changing status: it opens a service account and moves
   * what the deposit schedule asks of the customer. A screen that refreshed only the queue would
   * leave a 360° page beside it insisting the customer has no account.
   */
  function refresh() {
    void queryClient.invalidateQueries({ queryKey: applicationKeys.all });
    void queryClient.invalidateQueries({ queryKey: customerKeys.all });
    void queryClient.invalidateQueries({ queryKey: ['service-accounts'] });
  }

  const pickUp = useMutation({
    mutationFn: () => applicationsApi.startReview(application.id),
    onSuccess: (updated) => {
      toast.success(`${updated.applicationNumber} is under review.`);
      refresh();
    },
    onError: (error) => toast.apiError(error, 'That application could not be picked up.'),
  });

  return (
    <div className="space-y-6">
      <DetailList
        items={[
          {
            label: 'Customer',
            value: (
              <Link className="text-primary font-medium hover:underline" to={`/customers/${application.customerId}`}>
                View customer
              </Link>
            ),
          },
          { label: 'Service', value: serviceTypeLabel(application.serviceType) },
          { label: 'Supply wanted from', value: formatDate(application.requestedOn) },
          { label: 'Filed', value: formatDateTime(application.submittedAt) },
          { label: 'Filed by', value: orNotRecorded(application.submittedByName ?? application.submittedById) },
          { label: 'Reviewer', value: orNotRecorded(application.reviewerName ?? application.reviewerId) },
          { label: 'Notes', value: orNotRecorded(application.notes), wide: true },
        ]}
      />

      {application.decidedAt && (
        <DetailList
          items={[
            { label: 'Decided', value: formatDateTime(application.decidedAt) },
            { label: 'Decided by', value: orNotRecorded(application.decidedByName ?? application.decidedById) },
            {
              label: 'Reason',
              value: application.decisionReasonCode
                ? applicationReasonLabel(application.decisionReasonCode)
                : orNotRecorded(null),
            },
            {
              label: 'Account opened',
              value: application.serviceAccountId ? (
                <Link
                  className="text-primary font-medium hover:underline"
                  to={`/customers/${application.customerId}`}
                >
                  View account
                </Link>
              ) : (
                orNotRecorded(null)
              ),
            },
            { label: 'Decision notes', value: orNotRecorded(application.decisionNotes), wide: true },
          ]}
        />
      )}

      <ChecklistSection application={application} />

      {application.isOpen && <UploadSection application={application} onUploaded={refresh} />}

      {isWaitingToBePickedUp(application) && (
        <section className="border-border bg-canvas rounded-card border p-4">
          <p className="text-body text-[13px]">
            Nobody is reviewing this yet. Picking it up is what makes a decision possible — CUC
            reviews an application before it establishes an account.
          </p>
          <Button className="mt-3" onClick={() => pickUp.mutate()} disabled={pickUp.isPending}>
            {pickUp.isPending ? 'Picking up…' : 'Start review'}
          </Button>
        </section>
      )}

      {application.isOpen && !isWaitingToBePickedUp(application) && (
        <DecisionSection
          application={application}
          reasonsFor={(decision) => reasonsForDecision(reference.data, decision)}
          needsNotes={(code) => reasonNeedsNotes(reference.data, code)}
          onDecided={() => {
            refresh();
            onDecided();
          }}
        />
      )}

      {!application.isOpen && application.status !== 'Approved' && (
        <ResubmitSection application={application} onResubmitted={refresh} />
      )}
    </div>
  );
}

/** What the application must carry, against what has arrived. */
function ChecklistSection({ application }: { application: ServiceApplication }) {
  return (
    <section>
      <h3 className="text-heading text-[15px] font-semibold">Required documents</h3>
      <ul className="border-border divide-border mt-3 divide-y rounded-card border">
        {application.checklist.map((line) => {
          const document = application.documents.find((candidate) => candidate.id === line.documentId);

          return (
            <li key={line.kind} className="flex items-center justify-between gap-3 px-4 py-3">
              <span className="flex min-w-0 items-center gap-2.5">
                <span
                  aria-hidden="true"
                  className={
                    line.isSatisfied
                      ? 'bg-success-soft text-success flex size-6 shrink-0 items-center justify-center rounded-full'
                      : 'bg-warning-soft text-warning flex size-6 shrink-0 items-center justify-center rounded-full'
                  }
                >
                  {line.isSatisfied ? <Check className="size-3.5" strokeWidth={2.5} /> : <FileText className="size-3.5" strokeWidth={1.75} />}
                </span>
                <span className="min-w-0">
                  <span className="text-heading block truncate text-[13px] font-medium">
                    {documentKindLabel(line.kind)}
                  </span>
                  <span className="text-muted block truncate text-xs">
                    {line.isSatisfied && line.uploadedAt
                      ? `Received ${formatDateTime(line.uploadedAt)}`
                      : 'Outstanding'}
                  </span>
                </span>
              </span>

              {/*
                A link, not a fetch. The browser renders a PDF or an image; downloading the scan into
                memory to show it would be work for nothing. The host AUDITS this read and gates it on
                customers.documents — narrower than the customers.read that opened this page — so a
                clerk who may see the checklist and not the identity page behind it gets a 403 here,
                which is the intended behaviour.
              */}
              {document && (
                <a
                  className="text-primary shrink-0 text-[13px] font-medium hover:underline"
                  href={applicationsApi.documentUrl(application.id, document.id)}
                  target="_blank"
                  rel="noreferrer"
                >
                  View
                </a>
              )}
            </li>
          );
        })}
      </ul>

      {isBlockedByChecklist(application) && application.isOpen && (
        <p className="text-warning mt-2 text-xs">
          Still outstanding: {describeMissingDocuments(application)}. Approval is blocked until these
          arrive, or is recorded as an exception that says why.
        </p>
      )}

      <OtherDocuments application={application} />
    </section>
  );
}

/** Anything attached that answers no checklist line — a landlord's letter, a site plan. */
function OtherDocuments({ application }: { application: ServiceApplication }) {
  const satisfying = new Set(application.checklist.map((line) => line.documentId));
  const rest = application.documents.filter((document) => !satisfying.has(document.id));

  if (rest.length === 0) return null;

  return (
    <ul className="mt-3 space-y-2">
      {rest.map((document) => (
        <li key={document.id} className="flex items-center justify-between gap-3">
          <span className="text-body flex min-w-0 items-center gap-2 text-[13px]">
            <Paperclip className="text-muted size-4 shrink-0" strokeWidth={1.75} aria-hidden="true" />
            <span className="truncate">{document.fileName}</span>
            <span className="text-muted shrink-0 text-xs">{documentKindLabel(document.kind)}</span>
          </span>
          <a
            className="text-primary shrink-0 text-[13px] font-medium hover:underline"
            href={applicationsApi.documentUrl(application.id, document.id)}
            target="_blank"
            rel="noreferrer"
          >
            View
          </a>
        </li>
      ))}
    </ul>
  );
}

/** The scanner's end of the desk: pick what the file is, attach it. */
function UploadSection({
  application,
  onUploaded,
}: {
  application: ServiceApplication;
  onUploaded: () => void;
}) {
  const reference = useApplicationReference();
  const fileRef = useRef<HTMLInputElement>(null);

  const kinds = useMemo(
    () => uploadableKinds(application, reference.data),
    [application, reference.data],
  );

  // The first thing still outstanding, because that is what a reviewer is reaching for.
  const [kind, setKind] = useState<ApplicationDocumentKind>(application.missingDocuments[0] ?? kinds[0]);

  const attach = useMutation({
    mutationFn: (file: File) => applicationsApi.attachDocument(application.id, kind, file),
    onSuccess: (document) => {
      toast.success(`${document.fileName} attached as ${documentKindLabel(document.kind)}.`);
      if (fileRef.current) fileRef.current.value = '';
      onUploaded();
    },
    onError: (error) => toast.apiError(error, 'That document could not be attached.'),
  });

  return (
    <section className="border-border rounded-card border p-4">
      <h3 className="text-heading text-[15px] font-semibold">Attach a document</h3>

      <div className="mt-3 flex flex-wrap items-end gap-3">
        <div className="min-w-0">
          <Label htmlFor="application-document-kind">Document</Label>
          <Select
            id="application-document-kind"
            className="mt-1.5"
            value={kind}
            onChange={(event) => setKind(event.target.value as ApplicationDocumentKind)}
          >
            {kinds.map((option) => (
              <option key={option} value={option}>
                {documentKindLabel(option)}
              </option>
            ))}
          </Select>
        </div>

        <div className="min-w-0 flex-1">
          <Label htmlFor="application-document-file">File</Label>
          <input
            id="application-document-file"
            ref={fileRef}
            type="file"
            accept={reference.data?.allowedContentTypes.join(',')}
            className="border-border bg-card text-body rounded-control mt-1.5 block h-9 w-full border px-3 py-1.5 text-[13px] file:mr-3 file:border-0 file:bg-transparent file:p-0 file:text-[13px] file:font-medium"
            onChange={(event) => {
              const file = event.target.files?.[0];
              if (!file) return;

              // Refused before the bytes leave, not after. The host refuses both of these too and
              // has to — a browser check is a courtesy, not a gate — but a rep at a counter should
              // not wait for a ten-megabyte scan to cross an island connection to be told no.
              const refusal = rejectUpload(file, reference.data);

              if (refusal) {
                toast.error(refusal);
                event.target.value = '';
              }
            }}
          />
        </div>

        <Button
          variant="secondary"
          disabled={attach.isPending}
          onClick={() => {
            const file = fileRef.current?.files?.[0];

            if (!file) {
              toast.warning('Choose a file to attach.');
              return;
            }

            attach.mutate(file);
          }}
        >
          <Upload className="size-4" strokeWidth={1.75} aria-hidden="true" />
          {attach.isPending ? 'Attaching…' : 'Attach'}
        </Button>
      </div>
    </section>
  );
}

/** Approve, reject or withdraw — one form, because the host takes one body for all three. */
function DecisionSection({
  application,
  reasonsFor,
  needsNotes,
  onDecided,
}: {
  application: ServiceApplication;
  reasonsFor: (decision: ServiceApplicationStatus) => ApplicationReasonCode[];
  needsNotes: (code: ApplicationReasonCode | '') => boolean;
  onDecided: () => void;
}) {
  const decisions = availableDecisions(application);
  const [decision, setDecision] = useState<ServiceApplicationStatus>(decisions[0] ?? 'Approved');
  const [reasonCode, setReasonCode] = useState<ApplicationReasonCode | ''>('');
  const [notes, setNotes] = useState('');

  const reasons = reasonsFor(decision);
  const mustExplain = needsNotes(reasonCode);

  const decide = useMutation<ApplicationApproval | ServiceApplication>({
    mutationFn: () => {
      const body = { reasonCode: reasonCode as ApplicationReasonCode, notes: notes.trim() || undefined };

      switch (decision) {
        case 'Approved':
          return applicationsApi.approve(application.id, body);
        case 'Rejected':
          return applicationsApi.reject(application.id, body);
        default:
          return applicationsApi.withdraw(application.id, body);
      }
    },
    onSuccess: (result) => {
      // An approval hands back the account it opened and what the deposit now asks for; the other
      // two hand back the application. The message says which, because "approved" on its own leaves
      // a rep wondering whether they still have to open anything.
      if ('account' in result) {
        toast.success(
          `${result.application.applicationNumber} approved — account ${result.account.accountNumber} opened.`,
          result.deposit.shortfallAmount > 0
            ? `Deposit outstanding: ${result.deposit.shortfallAmount.toFixed(2)} ${result.deposit.currency}.`
            : 'The deposit held already covers what is required.',
        );
      } else {
        toast.success(`${result.applicationNumber} ${result.status.toLowerCase()}.`);
      }

      onDecided();
    },
    onError: (error) => toast.apiError(error, 'That decision could not be recorded.'),
  });

  const blocked = decision === 'Approved' && isBlockedByChecklist(application)
    && reasonCode !== 'ApprovedByException';

  return (
    <section className="border-border rounded-card border p-4">
      <h3 className="text-heading text-[15px] font-semibold">Decision</h3>

      <div className="mt-3 space-y-3">
        <div>
          <Label htmlFor="application-decision">Outcome</Label>
          <Select
            id="application-decision"
            fullWidth
            className="mt-1.5"
            value={decision}
            onChange={(event) => {
              setDecision(event.target.value as ServiceApplicationStatus);
              // The reason lists do not overlap, so a code chosen for a rejection would be refused
              // by the host if it survived a switch to an approval.
              setReasonCode('');
            }}
          >
            {decisions.map((option) => (
              <option key={option} value={option}>
                {applicationStatusLabel(option)}
              </option>
            ))}
          </Select>
        </div>

        <div>
          <Label htmlFor="application-reason">Reason</Label>
          <Select
            id="application-reason"
            fullWidth
            className="mt-1.5"
            value={reasonCode}
            onChange={(event) => setReasonCode(event.target.value as ApplicationReasonCode | '')}
          >
            <option value="">Choose a reason…</option>
            {reasons.map((code) => (
              <option key={code} value={code}>
                {applicationReasonLabel(code)}
              </option>
            ))}
          </Select>
        </div>

        <div>
          <Label htmlFor="application-decision-notes">Notes{mustExplain ? '' : ' (optional)'}</Label>
          <Textarea
            id="application-decision-notes"
            className="mt-1.5"
            rows={3}
            value={notes}
            onChange={(event) => setNotes(event.target.value)}
            placeholder={mustExplain ? 'Say what actually happened.' : ''}
          />
        </div>

        {blocked && (
          <p className="text-warning text-xs">
            {describeMissingDocuments(application)} still outstanding. Attach it, or record the
            approval as an exception — which has to say why.
          </p>
        )}

        <Button
          variant={decision === 'Rejected' ? 'destructive' : 'primary'}
          disabled={reasonCode === '' || (mustExplain && notes.trim() === '') || blocked || decide.isPending}
          onClick={() => decide.mutate()}
        >
          {decide.isPending ? 'Recording…' : `Record ${applicationStatusLabel(decision).toLowerCase()}`}
        </Button>
      </div>
    </section>
  );
}

/** A decided application never moves again; the way forward is a fresh one that names it. */
function ResubmitSection({
  application,
  onResubmitted,
}: {
  application: ServiceApplication;
  onResubmitted: () => void;
}) {
  const resubmit = useMutation({
    mutationFn: () => applicationsApi.resubmit(application.id, {}),
    onSuccess: (fresh) => {
      toast.success(
        `${fresh.applicationNumber} filed, replacing ${application.applicationNumber}.`,
        'The evidence does not carry over — a fresh review needs the documents produced again.',
      );
      onResubmitted();
    },
    onError: (error) => toast.apiError(error, 'A fresh application could not be filed.'),
  });

  return (
    <section className="border-border bg-canvas rounded-card border p-4">
      <p className="text-body text-[13px]">
        This application is {applicationStatusLabel(application.status).toLowerCase()} and will not
        move again. If the applicant comes back with what was missing, file a fresh application
        naming this one.
      </p>
      <Button className="mt-3" variant="secondary" disabled={resubmit.isPending} onClick={() => resubmit.mutate()}>
        {resubmit.isPending ? 'Filing…' : 'File a fresh application'}
      </Button>
    </section>
  );
}
