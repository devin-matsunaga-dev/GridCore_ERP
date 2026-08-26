import { zodResolver } from '@hookform/resolvers/zod';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Pencil, X } from 'lucide-react';
import { useCallback, useState } from 'react';
import { useForm, useWatch, type Resolver } from 'react-hook-form';
import {
  billDeliveryChannels,
  communicationLanguages,
  customerKeys,
  customersApi,
  type Customer,
  type CustomerProfile,
} from '@/api/customers';
import { toast } from '@/components/feedback/toast';
import { DetailList, orNotRecorded } from '@/components/registry/detail-list';
import { ErrorState } from '@/components/registry/error-state';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select } from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import { StatusPill } from '@/components/ui/status';
import { formatDate } from '@/lib/format';
import { IntakeField, IntakeFields } from '../registration/components/intake-field';
import {
  buildProfileInput,
  mailingAddressSourceLabel,
  profileSchema,
  profileValuesFrom,
  type ProfileValues,
} from '../contacts';

/**
 * Where post goes and how this customer wants to be written to.
 *
 * A description list, not a table: these are one subject's labelled fields, which is the shape the
 * 360's customer record already uses and the shape a register of like rows is not.
 *
 * **The mailing address on screen is the resolved one** — the override when there is one, the
 * service address otherwise — and the line under it says which, because "12 Sinapalo Drive" means
 * two quite different things depending on the answer. Clearing the override does not clear the
 * address; it sends post back to the premise, which is what the toggle is worded as.
 */
export function CustomerProfileCard({
  customer,
  profile,
  isLoading,
  error,
  onRetry,
}: {
  customer: Customer;
  profile: CustomerProfile | undefined;
  isLoading: boolean;
  error: unknown;
  onRetry: () => void;
}) {
  const [isEditing, setIsEditing] = useState(false);

  return (
    <Card>
      <CardHeader>
        <CardTitle>Mailing &amp; preferences</CardTitle>
        {profile && !isEditing && (
          <Button variant="secondary" size="sm" onClick={() => setIsEditing(true)}>
            <Pencil aria-hidden="true" />
            Edit
          </Button>
        )}
      </CardHeader>

      <CardContent>
        {error ? (
          <ErrorState error={error} onRetry={onRetry} />
        ) : isLoading || !profile ? (
          <div className="space-y-4">
            <Skeleton className="h-4 w-full" />
            <Skeleton className="h-4 w-2/3" />
          </div>
        ) : isEditing ? (
          <ProfileForm customer={customer} profile={profile} onDone={() => setIsEditing(false)} />
        ) : (
          <ProfileDetails profile={profile} />
        )}
      </CardContent>
    </Card>
  );
}

function ProfileDetails({ profile }: { profile: CustomerProfile }) {
  return (
    <DetailList
      columns={2}
      items={[
        {
          label: 'Mailing address',
          wide: true,
          value: (
            <span className="flex flex-wrap items-center gap-x-2 gap-y-1">
              {orNotRecorded(profile.formattedMailingAddress)}
              <StatusPill
                status={mailingAddressSourceLabel(profile)}
                tone={profile.source === 'None' ? 'warning' : profile.source === 'Override' ? 'info' : 'neutral'}
              />
            </span>
          ),
        },
        { label: 'Bill delivery', value: profile.billDeliveryChannel },
        { label: 'Preferred language', value: profile.preferredLanguage },
        { label: 'Outage notices', value: profile.outageNotices ? 'Wanted' : 'Declined' },
        { label: 'Dunning notices', value: profile.dunningNotices ? 'Wanted' : 'Declined' },
        {
          label: 'Preferences saved',
          wide: true,
          // Null is not "never touched by accident" — it is the honest statement that nobody has
          // expressed a preference and the customer is on the defaults.
          value: profile.updatedAt ? formatDate(profile.updatedAt) : <span className="text-muted">Still on the defaults</span>,
        },
      ]}
    />
  );
}

function ProfileForm({
  customer,
  profile,
  onDone,
}: {
  customer: Customer;
  profile: CustomerProfile;
  onDone: () => void;
}) {
  const queryClient = useQueryClient();

  const hasEmail = Boolean(customer.email && customer.email.trim().length > 0);

  // Built per validation from the customer being edited, because the email rule is a fact about the
  // customer record rather than about this form — the shape `intakeSchema` established.
  const resolver = useCallback<Resolver<ProfileValues>>(
    (values, context, options) => zodResolver(profileSchema({ hasEmail }))(values, context, options),
    [hasEmail],
  );

  const form = useForm<ProfileValues>({
    resolver,
    defaultValues: profileValuesFrom(profile),
    mode: 'onTouched',
  });

  const useServiceAddress = useWatch({ control: form.control, name: 'useServiceAddress' });

  const save = useMutation({
    mutationFn: (values: ProfileValues) => customersApi.saveProfile(customer.id, buildProfileInput(values)),
    onSuccess: (saved: CustomerProfile) => {
      toast.success('Preferences saved', mailingAddressSourceLabel(saved));

      queryClient.setQueryData(customerKeys.profile(customer.id), saved);
      onDone();
    },
    onError: (error) => toast.apiError(error, 'The preferences could not be saved.'),
  });

  const { errors } = form.formState;
  const addressErrors = errors.mailingAddress;

  return (
    <form className="space-y-5" onSubmit={form.handleSubmit((values) => save.mutate(values))}>
      <IntakeFields>
        <IntakeField
          label="Bill delivery"
          htmlFor="profile-channel"
          error={errors.billDeliveryChannel?.message}
          hint={hasEmail ? undefined : 'This customer has no email address, so only Post can be honoured.'}
        >
          <Select
            id="profile-channel"
            fullWidth
            className="h-10 w-full text-sm"
            {...form.register('billDeliveryChannel')}
          >
            {billDeliveryChannels.map((channel) => (
              <option key={channel} value={channel}>
                {channel}
              </option>
            ))}
          </Select>
        </IntakeField>

        <IntakeField label="Preferred language" htmlFor="profile-language">
          <Select id="profile-language" fullWidth className="h-10 w-full text-sm" {...form.register('preferredLanguage')}>
            {communicationLanguages.map((language) => (
              <option key={language} value={language}>
                {language}
              </option>
            ))}
          </Select>
        </IntakeField>
      </IntakeFields>

      <fieldset className="space-y-2.5">
        <legend className="text-muted text-[13px] font-medium">Notices</legend>

        <Checkbox id="profile-outage" label="Outage notices" {...form.register('outageNotices')} />
        <Checkbox id="profile-dunning" label="Reminders before collections" {...form.register('dunningNotices')} />
      </fieldset>

      <fieldset className="space-y-3">
        <legend className="text-muted text-[13px] font-medium">Mailing address</legend>

        <Checkbox
          id="profile-same-address"
          label="Post goes to the service address"
          hint={
            profile.serviceAddress
              ? `Currently ${profile.serviceAddress.line1}, ${profile.serviceAddress.city}`
              : 'This customer holds no service account, so there is nothing to fall back to.'
          }
          {...form.register('useServiceAddress')}
        />

        {!useServiceAddress && (
          <IntakeFields>
            <IntakeField label="Street address" htmlFor="profile-line1" error={addressErrors?.line1?.message}>
              <Input id="profile-line1" {...form.register('mailingAddress.line1')} aria-invalid={Boolean(addressErrors?.line1)} />
            </IntakeField>

            <IntakeField label="Unit or building" htmlFor="profile-line2" error={addressErrors?.line2?.message}>
              <Input id="profile-line2" {...form.register('mailingAddress.line2')} />
            </IntakeField>

            <IntakeField label="Village or town" htmlFor="profile-city" error={addressErrors?.city?.message}>
              <Input id="profile-city" {...form.register('mailingAddress.city')} aria-invalid={Boolean(addressErrors?.city)} />
            </IntakeField>

            <IntakeField label="Island" htmlFor="profile-region" error={addressErrors?.region?.message}>
              <Input id="profile-region" {...form.register('mailingAddress.region')} aria-invalid={Boolean(addressErrors?.region)} />
            </IntakeField>

            <IntakeField label="Postal code" htmlFor="profile-postal" error={addressErrors?.postalCode?.message}>
              <Input id="profile-postal" {...form.register('mailingAddress.postalCode')} />
            </IntakeField>

            <IntakeField label="Country" htmlFor="profile-country" error={addressErrors?.country?.message}>
              <Input id="profile-country" {...form.register('mailingAddress.country')} aria-invalid={Boolean(addressErrors?.country)} />
            </IntakeField>
          </IntakeFields>
        )}
      </fieldset>

      <div className="flex justify-end gap-2">
        <Button type="button" variant="secondary" onClick={onDone} disabled={save.isPending}>
          <X aria-hidden="true" />
          Cancel
        </Button>
        <Button type="submit" disabled={save.isPending}>
          {save.isPending ? 'Saving…' : 'Save preferences'}
        </Button>
      </div>
    </form>
  );
}

/**
 * A labelled checkbox.
 *
 * Hand-written for the same reason `Drawer` is: shadcn's checkbox is a Radix dependency the project
 * does not carry, and CONVENTIONS.md asks before adding one. A native input takes the register call
 * unchanged and comes with its own keyboard behaviour.
 */
function Checkbox({
  id,
  label,
  hint,
  ...props
}: { id: string; label: string; hint?: string } & React.ComponentProps<'input'>) {
  return (
    <div className="flex items-start gap-2.5">
      <input
        id={id}
        type="checkbox"
        className="border-border text-primary focus-visible:ring-primary/40 mt-0.5 size-4 shrink-0 rounded-[4px] focus-visible:ring-2 focus-visible:outline-none"
        {...props}
      />
      <div className="min-w-0">
        <Label htmlFor={id} className="text-body text-[13px] font-normal">
          {label}
        </Label>
        {hint && <p className="text-muted mt-0.5 text-xs">{hint}</p>}
      </div>
    </div>
  );
}
