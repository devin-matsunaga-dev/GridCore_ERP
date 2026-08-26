import { zodResolver } from '@hookform/resolvers/zod';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { MessageSquareText, Pin, PinOff, Plus, X } from 'lucide-react';
import { useCallback, useState } from 'react';
import { useForm, useWatch, type Resolver } from 'react-hook-form';
import { z } from 'zod';
import type { Bill } from '@/api/billing';
import {
  customerKeys,
  customersApi,
  noteKinds,
  noteLinkKinds,
  type CustomerNote,
  type CustomerNoteKind,
  type CustomerNoteLinkKind,
  type NoteLinkInput,
} from '@/api/customers';
import type { Payment } from '@/api/payments';
import { toast } from '@/components/feedback/toast';
import type { Column } from '@/components/registry/data-table';
import { EmptyState } from '@/components/registry/empty-state';
import { RegistryTableCard } from '@/components/registry/registry-table-card';
import { useTableState } from '@/components/registry/table-state';
import { Button } from '@/components/ui/button';
import { Card, CardContent } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { StatusPill } from '@/components/ui/status';
import { Textarea } from '@/components/ui/textarea';
import { formatDate } from '@/lib/format';
import { IntakeField, IntakeFields } from '../registration/components/intake-field';
import {
  correctionsByNote,
  followUpLabel,
  followUpStanding,
  followUpTone,
  noteKindLabel,
  noteKindTone,
  noteLinkLabel,
  sortCustomerNotes,
  todayInLocalTime,
} from '../notes';

/**
 * The customer's note log: what a rep wrote down, and every contact that took place.
 *
 * **Append-only, and the screen says so.** There is no edit button anywhere on this card, because
 * the host would refuse one — a note is corrected by writing a new note that references it, and the
 * row of a corrected note carries a "Corrected" pill rather than disappearing. A rep reading back a
 * dispute six months from now sees both what was first written and what replaced it, which is the
 * only reason a service record is worth keeping.
 *
 * A table, because it is a register of like rows — the owner's rule for this page, and the same
 * shape the bills, payments, contacts, deposits and service accounts all take.
 */
export function CustomerNotesCard({
  customerId,
  notes,
  bills,
  payments,
  isLoading,
  error,
  onRetry,
}: {
  customerId: string;
  notes: readonly CustomerNote[];
  /** This customer's bills, for the link select. Already fetched by the page — choosing one issues no request. */
  bills: readonly Bill[];
  /** This customer's payments, for the same reason. */
  payments: readonly Payment[];
  isLoading: boolean;
  error: unknown;
  onRetry: () => void;
}) {
  const [isLogging, setIsLogging] = useState(false);

  // The note being corrected, if any. A correction opens the same form with the original's words in
  // it, because a rep correcting a note is usually fixing a few of them.
  const [correcting, setCorrecting] = useState<CustomerNote | null>(null);

  const ordered = sortCustomerNotes(notes);
  const corrections = correctionsByNote(notes);

  const columns = noteColumns(corrections, customerId);
  const table = useTableState({ rows: ordered, columns });

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between gap-3">
        <h3 className="text-heading text-lg font-semibold">Notes and interactions</h3>

        <Button
          variant="secondary"
          size="sm"
          onClick={() => {
            setCorrecting(null);
            setIsLogging((logging) => !logging);
          }}
        >
          {isLogging ? <X aria-hidden="true" /> : <Plus aria-hidden="true" />}
          {isLogging ? 'Cancel' : 'Log a note'}
        </Button>
      </div>

      {(isLogging || correcting) && (
        <Card>
          <CardContent className="pt-6">
            <NoteForm
              customerId={customerId}
              correcting={correcting}
              bills={bills}
              payments={payments}
              onDone={() => {
                setIsLogging(false);
                setCorrecting(null);
              }}
            />
          </CardContent>
        </Card>
      )}

      <RegistryTableCard
        columns={columns}
        table={table}
        rowKey={(note) => note.id}
        label="Notes and interactions"
        isLoading={isLoading}
        error={error}
        onRetry={onRetry}
        onRowActivate={(note) => {
          setIsLogging(false);
          setCorrecting(note);
        }}
        isRowActive={(note) => note.id === correcting?.id}
        returnedRows={ordered.length}
        empty={
          <EmptyState
            icon={MessageSquareText}
            title="Nothing logged yet"
            message="Calls, counter visits, complaints and billing disputes are recorded here. Notes are append-only — a correction is a new note referencing the old one."
            action={
              <Button size="sm" onClick={() => setIsLogging(true)}>
                <Plus aria-hidden="true" />
                Log a note
              </Button>
            }
          />
        }
      />
    </div>
  );
}

/**
 * The columns.
 *
 * Built rather than declared as a constant because two of them close over state the card holds: the
 * corrections map, which is derived from the whole log, and the customer id the pin mutation
 * invalidates.
 */
function noteColumns(corrections: ReadonlyMap<string, CustomerNote>, customerId: string): Column<CustomerNote>[] {
  const today = todayInLocalTime();

  return [
    {
      key: 'recordedAt',
      header: 'When',
      sortValue: (note) => note.recordedAt,
      cell: (note) => <span className="text-body text-[13px] whitespace-nowrap">{formatDate(note.recordedAt)}</span>,
    },
    {
      key: 'kind',
      header: 'Kind',
      primary: true,
      sortValue: (note) => note.kind,
      cell: (note) => <StatusPill status={noteKindLabel(note.kind)} tone={noteKindTone(note.kind)} />,
    },
    {
      key: 'body',
      header: 'Note',
      wide: true,
      sortValue: (note) => note.body,
      cell: (note) => (
        <div className="min-w-0 space-y-1">
          <p className="text-body text-[13px]">{note.body}</p>

          <div className="flex flex-wrap items-center gap-1.5">
            {/*
              Both derived, never stored: the corrections map is built from the log because the host
              deliberately keeps no back-pointer on an immutable row.
            */}
            {corrections.has(note.id) && <StatusPill status="Corrected" tone="neutral" />}
            {note.correctsNoteId !== null && <StatusPill status="Correction" tone="info" />}
          </div>
        </div>
      ),
    },
    {
      key: 'link',
      header: 'About',
      // Its own column and never wrapping — the owner's rule from WP-2.10. A registry number broken
      // across two lines reads as two numbers.
      sortValue: (note) => noteLinkLabel(note),
      cell: (note) => {
        const label = noteLinkLabel(note);

        return label ? (
          <span className="tabular text-body text-[13px] whitespace-nowrap">{label}</span>
        ) : (
          <span className="text-muted">—</span>
        );
      },
    },
    {
      key: 'followUpOn',
      header: 'Follow-up',
      sortValue: (note) => note.followUpOn,
      cell: (note) => {
        const standing = followUpStanding(note, today);

        if (standing === 'none') return <span className="text-muted">—</span>;

        return (
          <span className="flex flex-wrap items-center gap-1.5 whitespace-nowrap">
            <StatusPill status={followUpLabel(standing)!} tone={followUpTone(standing)} />
            <span className="text-muted text-[13px]">{formatDate(note.followUpOn!)}</span>
          </span>
        );
      },
    },
    {
      key: 'actor',
      header: 'By',
      sortValue: (note) => note.actorName ?? note.actorId,
      cell: (note) => <span className="text-muted text-[13px]">{note.actorName ?? note.actorId}</span>,
    },
    {
      key: 'pin',
      header: 'Pinned',
      align: 'right',
      sortValue: (note) => note.isPinned,
      cell: (note) => <PinButton note={note} customerId={customerId} />,
    },
  ];
}

/**
 * The one thing about a note that moves.
 *
 * Pinning is a shelf, not a sentence — it decides where a note sits and says nothing about what
 * happened, which is why it is the single mutable field on an otherwise immutable row. Idempotent on
 * the host too, so a double click is not a 409.
 */
function PinButton({ note, customerId }: { note: CustomerNote; customerId: string }) {
  const queryClient = useQueryClient();

  const pin = useMutation({
    mutationFn: () => customersApi.pinNote(note.id, !note.isPinned),
    onSuccess: (updated) => {
      void queryClient.invalidateQueries({ queryKey: customerKeys.notesFor(customerId) });

      toast.success(
        updated.isPinned ? 'Pinned to the top of the log' : 'Unpinned',
        updated.isPinned ? 'It will stay above the rest whatever its date.' : 'It keeps its place in the log.',
      );
    },
    onError: (error) => toast.apiError(error, 'The note could not be pinned.'),
  });

  const Icon = note.isPinned ? Pin : PinOff;

  return (
    <Button
      variant="ghost"
      size="icon"
      // The row itself opens the correction form, so a click on the pin must not do both.
      onClick={(event) => {
        event.stopPropagation();
        pin.mutate();
      }}
      disabled={pin.isPending}
      aria-pressed={note.isPinned}
      aria-label={note.isPinned ? 'Unpin this note' : 'Pin this note to the top'}
      title={note.isPinned ? 'Unpin this note' : 'Pin this note to the top'}
    >
      <Icon aria-hidden="true" className={note.isPinned ? 'text-primary' : 'text-muted'} />
    </Button>
  );
}

type NoteValues = {
  kind: CustomerNoteKind;
  body: string;
  followUpOn: string;
  linkKind: CustomerNoteLinkKind | '';
  linkEntityId: string;
};

/**
 * The form, for a new note and for a correction alike.
 *
 * One form because a correction *is* a note — it says what the original should have said, and the
 * host files it where the original was filed. What changes is the endpoint it posts to and the
 * sentence above it.
 *
 * **The browser refuses what the host would refuse, deliberately duplicating the rules** — WP-2.8's
 * call, for the same reason: the host stays the authority, and the duplication buys the rep the
 * answer at the moment it becomes wrong rather than as a 400 after they have pressed the button. The
 * schema is built per validation from the values being validated, because whether a row has to be
 * chosen depends on which register was picked.
 */
function NoteForm({
  customerId,
  correcting,
  bills,
  payments,
  onDone,
}: {
  customerId: string;
  correcting: CustomerNote | null;
  bills: readonly Bill[];
  payments: readonly Payment[];
  onDone: () => void;
}) {
  const queryClient = useQueryClient();

  const resolver = useCallback<Resolver<NoteValues>>(
    (values, context, options) => zodResolver(noteSchema(values))(values, context, options),
    [],
  );

  const form = useForm<NoteValues>({
    resolver,
    defaultValues: {
      kind: correcting?.kind ?? 'InboundCall',
      body: correcting?.body ?? '',

      // A correction does NOT inherit the original's follow-up date: it may well be in the past by
      // now, which the host refuses, and a rep correcting the words of a note has not necessarily
      // said anything about when somebody should ring back.
      followUpOn: '',
      linkKind: correcting?.linkKind ?? '',
      linkEntityId: correcting?.linkedEntityId ?? '',
    },
    mode: 'onTouched',
  });

  const write = useMutation({
    mutationFn: (values: NoteValues) => {
      const link: NoteLinkInput | null =
        values.linkKind === '' ? null : { kind: values.linkKind, entityId: values.linkEntityId };

      const followUpOn = values.followUpOn === '' ? null : values.followUpOn;

      return correcting === null
        ? customersApi.logNote(customerId, { kind: values.kind, body: values.body.trim(), followUpOn, link })
        : customersApi.correctNote(correcting.id, { kind: values.kind, body: values.body.trim(), followUpOn, link });
    },
    onSuccess: (note) => {
      toast.success(
        correcting === null ? `${noteKindLabel(note.kind)} logged` : 'Correction recorded',
        correcting === null
          ? 'It is on the customer’s log and their timeline.'
          : 'The note it corrects is unchanged — both stay on the log.',
      );

      // The log and the timeline both move. The timeline reads the same fetch, so one invalidation
      // covers it.
      void queryClient.invalidateQueries({ queryKey: customerKeys.notesFor(customerId) });

      onDone();
    },
    onError: (error) => toast.apiError(error, 'The note could not be saved.'),
  });

  const { errors } = form.formState;

  // `useWatch` rather than `form.watch()`: the latter returns a new value on every render, which
  // React Compiler cannot memoize past.
  const linkKind = useWatch({ control: form.control, name: 'linkKind' });

  return (
    <form className="space-y-4" onSubmit={form.handleSubmit((values) => write.mutate(values))}>
      <div className="space-y-1">
        <p className="text-heading text-[15px] font-semibold">
          {correcting === null ? 'Log a note or an interaction' : 'Correct an earlier note'}
        </p>

        {correcting !== null && (
          <p className="text-muted text-[13px]">
            {/*
              Said plainly, because it is the rule of this screen a rep is most likely to be
              surprised by — and because they are about to press a button labelled "Record
              correction" on a register that will keep both versions.
            */}
            This writes a new note referencing the one you chose. The original stays on the log
            exactly as it was written.
          </p>
        )}
      </div>

      <IntakeFields>
        <IntakeField label="Kind" htmlFor="note-kind" error={errors.kind?.message}>
          <Select id="note-kind" fullWidth {...form.register('kind')}>
            {noteKinds.map((kind) => (
              <option key={kind} value={kind}>
                {noteKindLabel(kind)}
              </option>
            ))}
          </Select>
        </IntakeField>

        <IntakeField
          label="Follow-up"
          htmlFor="note-follow-up"
          error={errors.followUpOn?.message}
          hint="Optional. The day somebody has to come back to this; it cannot be in the past."
        >
          <Input id="note-follow-up" type="date" min={todayInLocalTime()} {...form.register('followUpOn')} />
        </IntakeField>

        <IntakeField label="About" htmlFor="note-link-kind" error={errors.linkKind?.message}>
          <Select
            id="note-link-kind"
            fullWidth
            {...form.register('linkKind', {
              // Changing the register clears the row: a bill id left over in a payment select is a
              // 400 the rep did not type.
              onChange: () => form.setValue('linkEntityId', ''),
            })}
          >
            <option value="">Nothing in particular</option>
            {noteLinkKinds.map((kind) => (
              <option key={kind} value={kind}>
                {kind === 'WorkOrder' ? 'Work order' : kind}
              </option>
            ))}
          </Select>
        </IntakeField>

        {linkKind !== '' && (
          <IntakeField
            label={linkKind === 'WorkOrder' ? 'Work order' : linkKind}
            htmlFor="note-link-entity"
            error={errors.linkEntityId?.message}
            hint={
              linkKind === 'WorkOrder'
                ? 'Work orders arrive in a later release, so this identifier is stored as given rather than checked.'
                : undefined
            }
          >
            {linkKind === 'WorkOrder' ? (
              // A free-text id rather than a select, because there is no register to list. The host
              // stores it unverified until WP-3.1 builds one — the one place that gap reaches a rep.
              <Input id="note-link-entity" {...form.register('linkEntityId')} aria-invalid={Boolean(errors.linkEntityId)} />
            ) : (
              <Select id="note-link-entity" fullWidth {...form.register('linkEntityId')}>
                <option value="">Choose one</option>
                {linkKind === 'Bill'
                  ? bills.map((bill) => (
                      <option key={bill.id} value={bill.id}>
                        {bill.billNumber}
                      </option>
                    ))
                  : payments.map((payment) => (
                      <option key={payment.id} value={payment.id}>
                        {payment.paymentNumber} — {payment.status}
                      </option>
                    ))}
              </Select>
            )}
          </IntakeField>
        )}
      </IntakeFields>

      <IntakeField label="Note" htmlFor="note-body" error={errors.body?.message}>
        <Textarea
          id="note-body"
          rows={4}
          placeholder="What was said, in your own words."
          {...form.register('body')}
          aria-invalid={Boolean(errors.body)}
        />
      </IntakeField>

      <div className="flex justify-end gap-2">
        <Button type="button" variant="secondary" onClick={onDone} disabled={write.isPending}>
          Cancel
        </Button>
        <Button type="submit" disabled={write.isPending}>
          {write.isPending
            ? correcting === null
              ? 'Logging…'
              : 'Recording…'
            : correcting === null
              ? 'Log note'
              : 'Record correction'}
        </Button>
      </div>
    </form>
  );
}

/**
 * The rules, built from the values being validated.
 *
 * Every one of them is also enforced by the host — this is not the authority, it is the answer
 * arriving before the request does. The follow-up floor is the rep's own calendar day where the
 * host's is UTC; the disagreement is a few hours around midnight and can only admit a follow-up
 * early, never refuse one they meant.
 */
function noteSchema(values: NoteValues) {
  const today = todayInLocalTime();

  return z.object({
    kind: z.enum(noteKinds),

    body: z
      .string()
      .trim()
      .min(1, 'A note must say something.')
      .max(4000, 'Shorten the note.'),

    followUpOn: z
      .string()
      .refine((raw) => raw === '' || raw >= today, 'A follow-up cannot be in the past.'),

    linkKind: z.union([z.enum(noteLinkKinds), z.literal('')]),

    linkEntityId: z
      .string()
      .refine(
        (raw) => values.linkKind === '' || raw.trim().length > 0,
        values.linkKind === 'WorkOrder' ? 'Enter the work order identifier.' : 'Choose which one.',
      ),
  });
}
