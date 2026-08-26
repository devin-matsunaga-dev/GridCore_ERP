import { zodResolver } from '@hookform/resolvers/zod';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Plus, UserRound, X } from 'lucide-react';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { customerKeys, customersApi, type CustomerContact } from '@/api/customers';
import { toast } from '@/components/feedback/toast';
import type { Column } from '@/components/registry/data-table';
import { EmptyState } from '@/components/registry/empty-state';
import { RegistryTableCard } from '@/components/registry/registry-table-card';
import { useTableState } from '@/components/registry/table-state';
import { Button } from '@/components/ui/button';
import { Card, CardContent } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { StatusPill } from '@/components/ui/status';
import { formatDate } from '@/lib/format';
import { IntakeField, IntakeFields } from '../registration/components/intake-field';
import { CustomerContactDrawer } from './customer-contact-drawer';
import {
  authorisationLabel,
  contactSchema,
  emptyContact,
  primaryMethodSummary,
  sortContacts,
  type ContactValues,
} from '../contacts';

/**
 * Everybody a rep may speak to about this customer, as a table.
 *
 * A table because it is a register of like rows — the owner's rule on this page, and the reason the
 * service accounts stopped being a card grid. The rest of a contact is a drawer, so opening one does
 * not lose the list.
 *
 * The rows arrive pre-sorted by `sortContacts` — the people the account may be discussed with first
 * — which is the order a rep wants before they have touched a column header. The `Matched on`
 * lesson from WP-2.9 applies to the columns: an identifier gets its own column and never wraps.
 */
export function CustomerContactsCard({
  customerId,
  contacts,
  isLoading,
  error,
  onRetry,
}: {
  customerId: string;
  contacts: readonly CustomerContact[];
  isLoading: boolean;
  error: unknown;
  onRetry: () => void;
}) {
  const [openId, setOpenId] = useState<string | null>(null);
  const [isAdding, setIsAdding] = useState(false);

  const columns = contactColumns();
  const table = useTableState({ rows: sortContacts(contacts), columns });

  const open = contacts.find((contact) => contact.id === openId) ?? null;

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between gap-3">
        <h3 className="text-heading text-lg font-semibold">Contacts</h3>

        <Button variant="secondary" size="sm" onClick={() => setIsAdding((adding) => !adding)}>
          {isAdding ? <X aria-hidden="true" /> : <Plus aria-hidden="true" />}
          {isAdding ? 'Cancel' : 'Add contact'}
        </Button>
      </div>

      {isAdding && (
        <Card>
          <CardContent className="pt-6">
            <AddContactForm customerId={customerId} onDone={() => setIsAdding(false)} />
          </CardContent>
        </Card>
      )}

      <RegistryTableCard
        columns={columns}
        table={table}
        rowKey={(contact) => contact.id}
        label="Contacts"
        isLoading={isLoading}
        error={error}
        onRetry={onRetry}
        onRowActivate={(contact) => setOpenId(contact.id)}
        isRowActive={(contact) => contact.id === openId}
        returnedRows={contacts.length}
        empty={
          <EmptyState
            icon={UserRound}
            title="No contacts yet"
            message="These are the additional people a rep may speak to. The customer's own name, email and telephone stay on the customer record."
            action={
              <Button size="sm" onClick={() => setIsAdding(true)}>
                <Plus aria-hidden="true" />
                Add contact
              </Button>
            }
          />
        }
      />

      <CustomerContactDrawer contact={open} customerId={customerId} onClose={() => setOpenId(null)} />
    </div>
  );
}

function contactColumns(): Column<CustomerContact>[] {
  return [
    {
      key: 'name',
      header: 'Name',
      primary: true,
      sortValue: (contact) => contact.name,
      cell: (contact) => <span className="text-heading font-medium">{contact.name}</span>,
    },
    {
      key: 'relationship',
      header: 'Relationship',
      sortValue: (contact) => contact.relationship,
      cell: (contact) => <span className="text-body">{contact.relationship ?? <span className="text-muted">—</span>}</span>,
    },
    {
      key: 'methods',
      header: 'Primary contact',
      wide: true,
      // One primary per kind, not every number: a row is what a rep reads while the caller is
      // talking, and the rest is one click away in the drawer.
      sortValue: (contact) => primaryMethodSummary(contact),
      cell: (contact) => {
        const summary = primaryMethodSummary(contact);

        return summary ? (
          <span className="text-body text-[13px] whitespace-nowrap">{summary}</span>
        ) : (
          <span className="text-muted text-[13px]">Nothing recorded</span>
        );
      },
    },
    {
      key: 'authorised',
      header: 'Disclosure',
      sortValue: (contact) => contact.isAuthorisedToDiscuss,
      cell: (contact) => (
        <StatusPill
          status={authorisationLabel(contact)}
          tone={contact.isAuthorisedToDiscuss ? 'success' : 'neutral'}
        />
      ),
    },
    {
      key: 'recordedAt',
      header: 'Added',
      sortValue: (contact) => contact.recordedAt,
      cell: (contact) => <span className="text-muted text-[13px] whitespace-nowrap">{formatDate(contact.recordedAt)}</span>,
    },
  ];
}

/**
 * Adding a contact.
 *
 * Name and relationship only — the numbers are added in the drawer once the contact exists, because
 * a form that collected both would have to invent a client-side copy of "one primary per kind" to
 * decide which of three typed numbers wins.
 */
function AddContactForm({ customerId, onDone }: { customerId: string; onDone: () => void }) {
  const queryClient = useQueryClient();

  const form = useForm<ContactValues>({
    resolver: zodResolver(contactSchema),
    defaultValues: emptyContact,
    mode: 'onTouched',
  });

  const add = useMutation({
    mutationFn: (values: ContactValues) =>
      customersApi.addContact(customerId, {
        name: values.name,
        relationship: values.relationship?.trim() || null,
        isAuthorisedToDiscuss: values.isAuthorisedToDiscuss,
      }),
    onSuccess: (contact) => {
      toast.success(`${contact.name} added`, 'Add their numbers from the contact row.');

      void queryClient.invalidateQueries({ queryKey: customerKeys.contacts(customerId) });
      form.reset(emptyContact);
      onDone();
    },
    onError: (error) => toast.apiError(error, 'The contact could not be added.'),
  });

  const { errors } = form.formState;

  return (
    <form className="space-y-4" onSubmit={form.handleSubmit((values) => add.mutate(values))}>
      <IntakeFields>
        <IntakeField label="Name" htmlFor="new-contact-name" error={errors.name?.message}>
          <Input id="new-contact-name" {...form.register('name')} aria-invalid={Boolean(errors.name)} />
        </IntakeField>

        <IntakeField
          label="Relationship"
          htmlFor="new-contact-relationship"
          error={errors.relationship?.message}
          hint="Spouse, landlord, site manager."
        >
          <Input id="new-contact-relationship" {...form.register('relationship')} />
        </IntakeField>
      </IntakeFields>

      <div className="flex items-start gap-2.5">
        <input
          id="new-contact-authorised"
          type="checkbox"
          className="border-border text-primary focus-visible:ring-primary/40 mt-0.5 size-4 shrink-0 rounded-[4px] focus-visible:ring-2 focus-visible:outline-none"
          {...form.register('isAuthorisedToDiscuss')}
        />
        <div className="min-w-0">
          <label htmlFor="new-contact-authorised" className="text-body text-[13px]">
            May discuss the account
          </label>
          <p className="text-muted mt-0.5 text-xs">
            Needs the authorise permission. Leave it off and somebody who holds it can grant it later.
          </p>
        </div>
      </div>

      <div className="flex justify-end gap-2">
        <Button type="button" variant="secondary" onClick={onDone} disabled={add.isPending}>
          Cancel
        </Button>
        <Button type="submit" disabled={add.isPending}>
          {add.isPending ? 'Adding…' : 'Add contact'}
        </Button>
      </div>
    </form>
  );
}
