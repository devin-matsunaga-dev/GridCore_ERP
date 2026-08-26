import { zodResolver } from '@hookform/resolvers/zod';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { ArrowLeftRight } from 'lucide-react';
import { useMemo, useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import {
  customerKeys,
  customersApi,
  transitionReasonCodes,
  type AccountTransition,
  type Customer,
  type ServiceAccount,
  type ServiceLocation,
  type TransitionReasonCode,
} from '@/api/customers';
import { toast } from '@/components/feedback/toast';
import type { Column } from '@/components/registry/data-table';
import { EmptyState } from '@/components/registry/empty-state';
import { RegistryTableCard } from '@/components/registry/registry-table-card';
import { useTableState } from '@/components/registry/table-state';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { StatusPill, toneFor } from '@/components/ui/status';
import { formatDate, formatLabel, formatMoney } from '@/lib/format';
import { IntakeField, IntakeFields } from '../registration/components/intake-field';
import { todayInLocalTime } from '../notes';
import {
  describeTransition,
  isDated,
  movableAccounts,
  otherClass,
  sortTransitions,
  transitionKindLabel,
  transitionKindTone,
  transitionNeedsNotes,
  transitionReasonLabel,
  transitionReasonsFor,
} from '../transitions';

/**
 * The two changes that alter what a customer is billed (WP-2.15), and the register of every one
 * that has been made.
 *
 * **Five acts, one register.** A class change and a status change move the customer record; a
 * move-in, a move-out and a transfer move service between premises. Every one of them needs a reason
 * code from a fixed list and carries an effective date, which is what the billing pass will price
 * from — so the form asks for both before the host has to refuse the request for want of them.
 *
 * **There is no other way to do any of this in the product.** The customer edit form lost its class
 * field and the service account lost its close button, deliberately: a second way in is a way
 * without a reason code, which would make the register a partial record of what has happened.
 *
 * The register is a table, because it is a register of like rows — the owner's rule for this page,
 * and the shape the bills, payments, deposit movements, contacts and accounts all take.
 */
export function CustomerTransitionsCard({
  customer,
  accounts,
  premises,
  transitions,
  isLoading,
  error,
  onRetry,
}: {
  customer: Customer;
  accounts: readonly ServiceAccount[];
  premises: readonly ServiceLocation[];
  transitions: readonly AccountTransition[];
  isLoading: boolean;
  error: unknown;
  onRetry: () => void;
}) {
  const [act, setAct] = useState<TransitionAct | null>(null);

  const rows = useMemo(() => sortTransitions(transitions), [transitions]);
  const table = useTableState({ rows, columns: transitionColumns });

  const movable = useMemo(() => movableAccounts(accounts), [accounts]);

  // Active premises only. The host still refuses one that is already served, with a 409 naming the
  // account in the way — occupancy is a question about every customer's accounts and this page only
  // ever loaded one customer's, so it is the one refusal the browser cannot get ahead of.
  const openPremises = useMemo(() => premises.filter((premise) => premise.isActive), [premises]);

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader>
          <CardTitle>Account transitions</CardTitle>
        </CardHeader>
        <CardContent className="space-y-5">
          <div className="flex flex-wrap items-end justify-between gap-4">
            <div>
              <p className="text-muted text-[13px] font-medium">Billed as</p>
              <p className="text-heading mt-1.5 text-[30px] leading-none font-bold">
                {formatLabel(customer.class)}
              </p>
              <p className="text-muted mt-2 text-[13px]">
                {/*
                  A null effective date means "since registration" rather than "unknown", so it is
                  said in words. A back-dated re-classification is exactly the thing a rep needs to
                  see here without opening the register below.
                */}
                {customer.classEffectiveOn
                  ? `Commercial or residential since ${formatDate(customer.classEffectiveOn)}`
                  : 'On the class this customer was registered under'}
              </p>
            </div>

            <div className="flex flex-wrap items-center gap-2">
              <StatusPill status={formatLabel(customer.status)} tone={toneFor(customer.status)} />
            </div>
          </div>

          <div className="flex flex-wrap gap-2">
            <ActButton act="class" current={act} onPick={setAct}>
              Change to {otherClass(customer.class).toLowerCase()}
            </ActButton>

            <ActButton
              act="status"
              current={act}
              onPick={setAct}
              disabled={customer.allowedTransitions.length === 0}
              title={
                customer.allowedTransitions.length === 0
                  ? 'This customer is closed, which is terminal — reopening is a new registration.'
                  : undefined
              }
            >
              Change status
            </ActButton>

            <ActButton
              act="move-in"
              current={act}
              onPick={setAct}
              disabled={openPremises.length === 0}
              title={openPremises.length === 0 ? 'There is no active premise to move them in to.' : undefined}
            >
              Move in
            </ActButton>

            {/*
              Both of these need an account still holding a premise, and the title says so. A button
              that 409s on click is a button that made the rep find out the hard way — the call the
              deposit tab already made about a zero balance.
            */}
            <ActButton
              act="move-out"
              current={act}
              onPick={setAct}
              disabled={movable.length === 0}
              title={movable.length === 0 ? 'This customer has no open service account.' : undefined}
            >
              Move out
            </ActButton>

            <ActButton
              act="transfer"
              current={act}
              onPick={setAct}
              disabled={movable.length === 0 || openPremises.length === 0}
              title={
                movable.length === 0
                  ? 'This customer has no open service account to transfer.'
                  : openPremises.length === 0
                    ? 'There is no active premise to transfer them to.'
                    : undefined
              }
            >
              Transfer
            </ActButton>
          </div>

          {act && (
            <div className="border-border border-t pt-5">
              <TransitionForm
                act={act}
                customer={customer}
                accounts={movable}
                premises={openPremises}
                onDone={() => setAct(null)}
              />
            </div>
          )}
        </CardContent>
      </Card>

      <div className="space-y-4">
        <h3 className="text-heading text-lg font-semibold">History</h3>

        <RegistryTableCard
          columns={transitionColumns}
          table={table}
          rowKey={(transition) => transition.id}
          label="Account transitions"
          isLoading={isLoading}
          error={error}
          onRetry={onRetry}
          returnedRows={rows.length}
          empty={
            <EmptyState
              icon={ArrowLeftRight}
              title="No transitions recorded"
              message="Nothing has changed about what this customer is billed or where they are served. A class change, a status move or a house move records an entry here."
            />
          }
        />
      </div>
    </div>
  );
}

function ActButton({
  act,
  current,
  onPick,
  disabled,
  title,
  children,
}: {
  act: TransitionAct;
  current: TransitionAct | null;
  onPick: (act: TransitionAct | null) => void;
  disabled?: boolean;
  title?: string;
  children: React.ReactNode;
}) {
  const open = current === act;

  return (
    <Button
      variant={act === 'class' ? 'primary' : 'secondary'}
      size="sm"
      disabled={disabled}
      title={title}
      onClick={() => onPick(open ? null : act)}
    >
      {open ? 'Cancel' : children}
    </Button>
  );
}

const transitionColumns: Column<AccountTransition>[] = [
  {
    key: 'recordedAt',
    header: 'Recorded',
    sortValue: (transition) => transition.recordedAt,
    cell: (transition) => (
      <span className="text-body text-[13px] whitespace-nowrap">{formatDate(transition.recordedAt)}</span>
    ),
  },
  {
    key: 'kind',
    header: 'Transition',
    primary: true,
    sortValue: (transition) => transition.kind,
    cell: (transition) => (
      <StatusPill status={transitionKindLabel(transition.kind)} tone={transitionKindTone(transition.kind)} />
    ),
  },
  {
    key: 'change',
    header: 'Change',
    wide: true,
    sortValue: (transition) => describeTransition(transition),
    cell: (transition) => <span className="text-body text-[13px]">{describeTransition(transition)}</span>,
  },
  {
    key: 'effectiveOn',
    header: 'Effective',
    sortValue: (transition) => transition.effectiveOn,
    cell: (transition) => (
      <span className="text-body text-[13px] whitespace-nowrap">
        {formatDate(transition.effectiveOn)}
        {/*
          The mark a back-dated re-classification would otherwise hide behind: the row says "today"
          in the recorded column and prices from last month. Shown only when the two disagree, so the
          ordinary case says nothing at all.
        */}
        {isDated(transition) && <span className="text-muted"> · dated</span>}
      </span>
    ),
  },
  {
    key: 'reasonCode',
    header: 'Reason',
    sortValue: (transition) => transition.reasonCode,
    cell: (transition) => (
      <span className="text-body text-[13px]">{transitionReasonLabel(transition.reasonCode)}</span>
    ),
  },
  {
    key: 'depositCarried',
    header: 'Deposit carried',
    align: 'right',
    sortValue: (transition) => transition.depositCarried,
    cell: (transition) =>
      transition.depositCarried > 0 ? (
        <span className="tabular text-heading font-medium">{formatMoney(transition.depositCarried)}</span>
      ) : (
        // An em dash, not 0.00: nothing was carried, and a zero in a money column reads as a figure
        // somebody worked out rather than as a column that does not apply.
        <span className="text-muted">—</span>
      ),
  },
  {
    key: 'notes',
    header: 'Notes',
    wide: true,
    sortValue: (transition) => transition.notes,
    cell: (transition) => (
      <span className="text-body text-[13px]">{transition.notes ?? <span className="text-muted">—</span>}</span>
    ),
  },
  {
    key: 'actor',
    header: 'By',
    sortValue: (transition) => transition.actorName ?? transition.actorId,
    cell: (transition) => (
      <span className="text-muted text-[13px]">{transition.actorName ?? transition.actorId}</span>
    ),
  },
];

type TransitionAct = 'class' | 'status' | 'move-in' | 'move-out' | 'transfer';

const actKinds = {
  class: 'ClassChanged',
  status: 'StatusChanged',
  'move-in': 'MovedIn',
  'move-out': 'MovedOut',
  transfer: 'Transferred',
} as const;

const actLabels: Record<TransitionAct, { title: string; submit: string; pending: string }> = {
  class: { title: 'Change the customer class', submit: 'Change class', pending: 'Changing…' },
  status: { title: 'Change the customer status', submit: 'Change status', pending: 'Changing…' },
  'move-in': { title: 'Move the customer in at a premise', submit: 'Move in', pending: 'Moving in…' },
  'move-out': { title: 'End service at a premise', submit: 'Move out', pending: 'Moving out…' },
  transfer: { title: 'Transfer service to another premise', submit: 'Transfer', pending: 'Transferring…' },
};

type TransitionFormValues = {
  reasonCode: TransitionReasonCode;
  effectiveOn: string;
  notes: string;
  status: string;
  serviceAccountId: string;
  serviceLocationId: string;
};

/**
 * One transition's form.
 *
 * **The browser refuses what the host would refuse, deliberately duplicating the rules** — WP-2.8's
 * call, kept by WP-2.12: the host stays the authority, and the duplication buys the rep the answer
 * at the moment it becomes wrong rather than as a 400 after they have pressed the button. Only the
 * rules a browser can actually see are duplicated: how far back a class change may be dated depends
 * on when the customer was last billed, which is Billing's answer and stays a 409.
 */
function TransitionForm({
  act,
  customer,
  accounts,
  premises,
  onDone,
}: {
  act: TransitionAct;
  customer: Customer;
  accounts: readonly ServiceAccount[];
  premises: readonly ServiceLocation[];
  onDone: () => void;
}) {
  const queryClient = useQueryClient();
  const labels = actLabels[act];
  const kind = actKinds[act];
  const reasons = transitionReasonsFor(kind);

  const form = useForm<TransitionFormValues>({
    resolver: zodResolver(transitionSchema),
    defaultValues: {
      reasonCode: reasons[0],
      effectiveOn: todayInLocalTime(),
      notes: '',
      status: customer.allowedTransitions[0] ?? '',
      serviceAccountId: accounts[0]?.id ?? '',
      serviceLocationId: premises[0]?.id ?? '',
    },
    mode: 'onTouched',
  });

  const move = useMutation({
    mutationFn: (values: TransitionFormValues) => {
      const shared = {
        reasonCode: values.reasonCode,
        effectiveOn: values.effectiveOn,
        notes: values.notes.trim() || undefined,
      };

      switch (act) {
        case 'class':
          return customersApi.changeCustomerClass(customer.id, { ...shared, class: otherClass(customer.class) });
        case 'status':
          return customersApi.changeCustomerStatus(customer.id, {
            ...shared,
            status: values.status as Customer['status'],
          });
        case 'move-in':
          return customersApi.moveIn(customer.id, { ...shared, serviceLocationId: values.serviceLocationId });
        case 'move-out':
          return customersApi.moveOut(customer.id, { ...shared, serviceAccountId: values.serviceAccountId });
        default:
          return customersApi.transferService(customer.id, {
            ...shared,
            fromServiceAccountId: values.serviceAccountId,
            toServiceLocationId: values.serviceLocationId,
          });
      }
    },
    onSuccess: (transition) => {
      toast.success(
        transitionKindLabel(transition.kind),
        `${describeTransition(transition)} · effective ${formatDate(transition.effectiveOn)}.`,
      );

      // A transition moves more than its own register: the customer's class or status, the accounts
      // it opened or closed, and — on a transfer — the deposit ledger, which gains a carry that moves
      // nothing and still belongs on screen. Invalidating all four is what stops the header quoting
      // one thing while the tab below quotes another.
      void queryClient.invalidateQueries({ queryKey: customerKeys.transitionsFor(customer.id) });
      void queryClient.invalidateQueries({ queryKey: customerKeys.detail(customer.id) });
      void queryClient.invalidateQueries({ queryKey: customerKeys.deposits(customer.id) });
      void queryClient.invalidateQueries({ queryKey: ['service-accounts'] });

      onDone();
    },
    onError: (error) => toast.apiError(error, 'The transition could not be recorded.'),
  });

  const { errors } = form.formState;
  const reasonCode = form.watch('reasonCode');

  return (
    <form className="space-y-4" onSubmit={form.handleSubmit((values) => move.mutate(values))}>
      <p className="text-heading text-[15px] font-semibold">{labels.title}</p>

      <IntakeFields>
        {act === 'class' && (
          <IntakeField label="New class" htmlFor="transition-class">
            {/*
              There are two classes, so the target is stated rather than picked. A select of one
              useful option is a select that wastes a click and invites the other one, which the host
              refuses with a 409 for being the class already held.
            */}
            <Input id="transition-class" readOnly value={formatLabel(otherClass(customer.class))} />
          </IntakeField>
        )}

        {act === 'status' && (
          <IntakeField label="New status" htmlFor="transition-status" error={errors.status?.message}>
            {/*
              Only the moves WP-1.2's machine allows from where the customer stands. Offering the
              rest would be offering 409s — DESIGN.md's rule for every state machine in the product.
            */}
            <Select id="transition-status" fullWidth {...form.register('status')}>
              {customer.allowedTransitions.map((status) => (
                <option key={status} value={status}>
                  {formatLabel(status)}
                </option>
              ))}
            </Select>
          </IntakeField>
        )}

        {(act === 'move-out' || act === 'transfer') && (
          <IntakeField
            label={act === 'transfer' ? 'Account to transfer' : 'Account to close'}
            htmlFor="transition-account"
            error={errors.serviceAccountId?.message}
          >
            <Select id="transition-account" fullWidth {...form.register('serviceAccountId')}>
              {accounts.map((account) => (
                <option key={account.id} value={account.id}>
                  {account.accountNumber} — {formatLabel(account.status)}
                </option>
              ))}
            </Select>
          </IntakeField>
        )}

        {(act === 'move-in' || act === 'transfer') && (
          <IntakeField
            label={act === 'transfer' ? 'Premise to move to' : 'Premise'}
            htmlFor="transition-premise"
            error={errors.serviceLocationId?.message}
          >
            <Select id="transition-premise" fullWidth {...form.register('serviceLocationId')}>
              {premises.map((premise) => (
                <option key={premise.id} value={premise.id}>
                  {premise.locationCode} — {premise.address.line1}
                </option>
              ))}
            </Select>
          </IntakeField>
        )}

        <IntakeField label="Reason" htmlFor="transition-reason" error={errors.reasonCode?.message}>
          <Select id="transition-reason" fullWidth {...form.register('reasonCode')}>
            {reasons.map((code) => (
              <option key={code} value={code}>
                {transitionReasonLabel(code)}
              </option>
            ))}
          </Select>
        </IntakeField>

        <IntakeField
          label="Effective from"
          htmlFor="transition-effective"
          error={errors.effectiveOn?.message}
          hint="The day the change applies from — not the day it is recorded."
        >
          {/*
            NO min and NO max. How far BACK a transition may be dated depends on when the customer
            was last billed and when the account was opened, neither of which the browser knows; how
            far FORWARD is not limited at all, because a class change from the first of next month is
            the ordinary case. The host answers both with a 409 naming the date that is in the way.
          */}
          <Input id="transition-effective" type="date" {...form.register('effectiveOn')} />
        </IntakeField>

        <IntakeField
          label="Notes"
          htmlFor="transition-notes"
          error={errors.notes?.message}
          hint={transitionNeedsNotes(reasonCode) ? 'Required — say what actually happened.' : undefined}
        >
          <Input id="transition-notes" {...form.register('notes')} aria-invalid={Boolean(errors.notes)} />
        </IntakeField>
      </IntakeFields>

      <div className="flex flex-wrap gap-2">
        <Button type="submit" size="sm" disabled={move.isPending}>
          {move.isPending ? labels.pending : labels.submit}
        </Button>

        <Button type="button" variant="secondary" size="sm" onClick={onDone}>
          Cancel
        </Button>
      </div>
    </form>
  );
}

/**
 * The rules the browser can see.
 *
 * `Other` needing a sentence is the one that matters: the host refuses it silent, and asking here is
 * the difference between a form that says so before the rep presses save and one that answers with a
 * 400 afterwards.
 */
const transitionSchema = z
  .object({
    reasonCode: z.enum(transitionReasonCodes),
    effectiveOn: z.string().min(1, 'Say when this applies from.'),
    notes: z.string(),
    status: z.string(),
    serviceAccountId: z.string(),
    serviceLocationId: z.string(),
  })
  .refine(
    (values) => !transitionNeedsNotes(values.reasonCode) || values.notes.trim().length > 0,
    { path: ['notes'], message: 'Say what happened. A fixed list is only fixed if its escape hatch explains itself.' },
  );
