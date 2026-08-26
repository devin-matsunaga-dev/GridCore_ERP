import { zodResolver } from '@hookform/resolvers/zod';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Star, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import {
  contactMethodKinds,
  customerKeys,
  customersApi,
  type ContactMethodKind,
  type CustomerContact,
} from '@/api/customers';
import { toast } from '@/components/feedback/toast';
import { DetailList, orNotRecorded } from '@/components/registry/detail-list';
import { Drawer, DrawerSection } from '@/components/registry/drawer';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { StatusPill } from '@/components/ui/status';
import { formatDate } from '@/lib/format';
import { IntakeField } from '../registration/components/intake-field';
import {
  authorisationLabel,
  contactMethodSchema,
  contactSchema,
  contactValuesFrom,
  duplicatesExistingMethod,
  emptyContactMethod,
  methodKindLabel,
  methodsByKind,
  type ContactMethodValues,
  type ContactValues,
} from '../contacts';

/**
 * One contact's detail, over the contacts table.
 *
 * A drawer rather than a page, the call `ServiceAccountDrawer` already made: a rep comparing who is
 * on an account wants the list on screen while they read one row of it.
 *
 * **Every write here returns the whole contact**, which is why the drawer never reassembles one
 * from a response — promoting a method demotes another, and a screen that patched its own copy
 * would show two primaries until the next refetch.
 */
export function CustomerContactDrawer({
  contact,
  customerId,
  onClose,
}: {
  contact: CustomerContact | null;
  customerId: string;
  onClose: () => void;
}) {
  if (!contact) return null;

  return (
    <Drawer
      open
      onClose={onClose}
      title={contact.name}
      subtitle={
        <>
          <span className="text-muted text-[13px]">{contact.relationship ?? 'Relationship not recorded'}</span>
          <StatusPill
            status={authorisationLabel(contact)}
            tone={contact.isAuthorisedToDiscuss ? 'success' : 'neutral'}
          />
        </>
      }
    >
      <div className="space-y-6">
        <DrawerSection title="Contact">
          <ContactForm contact={contact} customerId={customerId} onRemoved={onClose} />
        </DrawerSection>

        <DrawerSection title="How to reach them">
          <ContactMethods contact={contact} customerId={customerId} />
        </DrawerSection>

        <DrawerSection title="Record">
          <DetailList
            items={[
              { label: 'Added', value: formatDate(contact.recordedAt) },
              { label: 'Relationship', value: orNotRecorded(contact.relationship) },
            ]}
          />
        </DrawerSection>
      </div>
    </Drawer>
  );
}

function ContactForm({
  contact,
  customerId,
  onRemoved,
}: {
  contact: CustomerContact;
  customerId: string;
  onRemoved: () => void;
}) {
  const queryClient = useQueryClient();

  const form = useForm<ContactValues>({
    resolver: zodResolver(contactSchema),
    defaultValues: contactValuesFrom(contact),
    mode: 'onTouched',
    // Keyed on the contact so opening another row's drawer reloads the form rather than showing the
    // previous contact's half-typed name against this one's heading.
    values: contactValuesFrom(contact),
  });

  const invalidate = () => queryClient.invalidateQueries({ queryKey: customerKeys.contacts(customerId) });

  const save = useMutation({
    mutationFn: (values: ContactValues) =>
      customersApi.updateContact(contact.id, {
        name: values.name,
        relationship: values.relationship?.trim() || null,
        isAuthorisedToDiscuss: values.isAuthorisedToDiscuss,
      }),
    onSuccess: (saved) => {
      toast.success(`${saved.name} saved`, authorisationLabel(saved));
      void invalidate();
    },
    // The 403 the host returns when a rep without `customers.authorise` moves the disclosure flag
    // arrives here as its own message — the shared toast already renders a permission refusal as one.
    onError: (error) => toast.apiError(error, 'The contact could not be saved.'),
  });

  const remove = useMutation({
    mutationFn: () => customersApi.removeContact(contact.id),
    onSuccess: () => {
      toast.success(`${contact.name} removed`);
      void invalidate();
      onRemoved();
    },
    onError: (error) => toast.apiError(error, 'The contact could not be removed.'),
  });

  const { errors } = form.formState;

  return (
    <form className="space-y-4" onSubmit={form.handleSubmit((values) => save.mutate(values))}>
      <IntakeField label="Name" htmlFor="contact-name" error={errors.name?.message}>
        <Input id="contact-name" {...form.register('name')} aria-invalid={Boolean(errors.name)} />
      </IntakeField>

      <IntakeField
        label="Relationship"
        htmlFor="contact-relationship"
        error={errors.relationship?.message}
        hint="Spouse, landlord, site manager — whatever they actually are."
      >
        <Input id="contact-relationship" {...form.register('relationship')} />
      </IntakeField>

      <div className="flex items-start gap-2.5">
        <input
          id="contact-authorised"
          type="checkbox"
          className="border-border text-primary focus-visible:ring-primary/40 mt-0.5 size-4 shrink-0 rounded-[4px] focus-visible:ring-2 focus-visible:outline-none"
          {...form.register('isAuthorisedToDiscuss')}
        />
        <div className="min-w-0">
          <label htmlFor="contact-authorised" className="text-body text-[13px]">
            May discuss the account
          </label>
          <p className="text-muted mt-0.5 text-xs">
            Moving this needs the authorise permission, and is recorded against your name.
          </p>
        </div>
      </div>

      <div className="flex justify-between gap-2">
        <Button
          type="button"
          variant="destructive"
          size="sm"
          onClick={() => remove.mutate()}
          disabled={remove.isPending}
        >
          <Trash2 aria-hidden="true" />
          Remove contact
        </Button>

        <Button type="submit" size="sm" disabled={save.isPending}>
          {save.isPending ? 'Saving…' : 'Save contact'}
        </Button>
      </div>
    </form>
  );
}

function ContactMethods({ contact, customerId }: { contact: CustomerContact; customerId: string }) {
  const queryClient = useQueryClient();

  const invalidate = () => queryClient.invalidateQueries({ queryKey: customerKeys.contacts(customerId) });

  const promote = useMutation({
    mutationFn: (methodId: string) => customersApi.makeContactMethodPrimary(contact.id, methodId),
    onSuccess: () => void invalidate(),
    onError: (error) => toast.apiError(error, 'That method could not be promoted.'),
  });

  const drop = useMutation({
    mutationFn: (methodId: string) => customersApi.removeContactMethod(contact.id, methodId),
    onSuccess: () => void invalidate(),
    onError: (error) => toast.apiError(error, 'That method could not be removed.'),
  });

  const groups = methodsByKind(contact);

  return (
    <div className="space-y-5">
      {groups.length === 0 ? (
        <p className="text-muted text-[13px]">No number or address recorded for this contact yet.</p>
      ) : (
        groups.map((group) => (
          <div key={group.kind} className="space-y-2">
            <p className="text-muted text-[11px] font-medium tracking-[0.06em] uppercase">
              {methodKindLabel(group.kind)}
            </p>

            <ul className="space-y-1.5">
              {group.methods.map((method) => (
                <li key={method.id} className="flex items-center justify-between gap-3">
                  <span className="text-body min-w-0 truncate text-[13px]">{method.value}</span>

                  <span className="flex shrink-0 items-center gap-1.5">
                    {method.isPrimary ? (
                      <StatusPill status="Primary" tone="success" />
                    ) : (
                      <Button
                        variant="ghost"
                        size="sm"
                        onClick={() => promote.mutate(method.id)}
                        disabled={promote.isPending}
                        aria-label={`Make ${method.value} the primary ${methodKindLabel(group.kind).toLowerCase()}`}
                      >
                        <Star aria-hidden="true" />
                        Make primary
                      </Button>
                    )}

                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={() => drop.mutate(method.id)}
                      disabled={drop.isPending}
                      aria-label={`Remove ${method.value}`}
                    >
                      <Trash2 aria-hidden="true" />
                    </Button>
                  </span>
                </li>
              ))}
            </ul>
          </div>
        ))
      )}

      <AddMethodForm contact={contact} customerId={customerId} />
    </div>
  );
}

function AddMethodForm({ contact, customerId }: { contact: CustomerContact; customerId: string }) {
  const queryClient = useQueryClient();
  const [duplicate, setDuplicate] = useState<string | null>(null);

  const form = useForm<ContactMethodValues>({
    resolver: zodResolver(contactMethodSchema),
    defaultValues: emptyContactMethod,
    mode: 'onTouched',
  });

  const add = useMutation({
    mutationFn: (values: ContactMethodValues) =>
      customersApi.addContactMethod(contact.id, { kind: values.kind, value: values.value }),
    onSuccess: () => {
      form.reset(emptyContactMethod);
      setDuplicate(null);
      void queryClient.invalidateQueries({ queryKey: customerKeys.contacts(customerId) });
    },
    onError: (error) => toast.apiError(error, 'That contact method could not be added.'),
  });

  function submit(values: ContactMethodValues) {
    // The host refuses a duplicate and this says so first, without a round trip. Compared exactly as
    // the aggregate compares — case aside, punctuation and all — so the two cannot disagree.
    if (duplicatesExistingMethod(contact, values.kind as ContactMethodKind, values.value)) {
      setDuplicate(`This contact already has that ${methodKindLabel(values.kind).toLowerCase()}.`);
      return;
    }

    setDuplicate(null);
    add.mutate(values);
  }

  return (
    <form className="border-border flex flex-wrap items-end gap-2 border-t pt-4" onSubmit={form.handleSubmit(submit)}>
      <IntakeField label="Kind" htmlFor="method-kind" className="w-28">
        <Select id="method-kind" fullWidth className="h-9 w-full text-sm" {...form.register('kind')}>
          {contactMethodKinds.map((kind) => (
            <option key={kind} value={kind}>
              {methodKindLabel(kind)}
            </option>
          ))}
        </Select>
      </IntakeField>

      <IntakeField
        label="Number or address"
        htmlFor="method-value"
        className="min-w-[12rem] flex-1"
        error={form.formState.errors.value?.message ?? duplicate ?? undefined}
      >
        <Input
          id="method-value"
          className="h-9"
          {...form.register('value')}
          aria-invalid={Boolean(form.formState.errors.value ?? duplicate)}
        />
      </IntakeField>

      <Button type="submit" size="sm" variant="secondary" disabled={add.isPending}>
        Add
      </Button>
    </form>
  );
}
