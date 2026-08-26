import type { UseFormReturn } from 'react-hook-form';
import { useServiceAccounts, useServiceLocations } from '@/api/customers';
import { Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import { availablePremises, type IntakeValues } from '../registration';
import { IntakeField, IntakeFields } from './intake-field';

/**
 * Step 2 — where supply is delivered.
 *
 * Two modes rather than two screens: a premise is either being added to the registry as part of the
 * intake, or it is already there and the account is being opened at it. Both are ordinary — a new
 * house and a new tenant at an old one — and the host takes exactly one of the two.
 *
 * The picker lists only premises that are active and not already served FOR THE SUPPLY BEING
 * APPLIED FOR (WP-2.17). That is a courtesy, not the rule: the host refuses a premise that already
 * takes that service with a 409 naming the account holding it, which is what makes the check correct
 * even when this list is a page behind.
 */
export function PremiseStep({ form }: { form: UseFormReturn<IntakeValues> }) {
  const { errors } = form.formState;
  const mode = form.watch('premiseMode');

  const locations = useServiceLocations({ isActive: true });
  const accounts = useServiceAccounts({}, mode === 'existing');

  // Narrowed to the supply being applied for: a premise already on electricity may still take a
  // water account, and hiding it would make the three-supply premise WP-2.17 exists for unreachable
  // from the wizard that opens accounts.
  const available = availablePremises(locations.data, accounts.data, form.watch('serviceType'));

  return (
    <div className="space-y-5">
      <IntakeField
        label="Premise"
        htmlFor="intake-premise-mode"
        hint="Register the address now, or open the account at a premise already on the books."
        className="max-w-sm"
      >
        <Select
          id="intake-premise-mode"
          fullWidth
          className="h-10 w-full text-sm"
          {...form.register('premiseMode')}
        >
          <option value="new">Register a new premise</option>
          <option value="existing">Use an existing premise</option>
        </Select>
      </IntakeField>

      {mode === 'new' ? (
        <IntakeFields>
          <IntakeField label="Street address" htmlFor="intake-line1" error={errors.line1?.message}>
            <Input id="intake-line1" {...form.register('line1')} aria-invalid={Boolean(errors.line1)} />
          </IntakeField>

          <IntakeField label="Unit or building" htmlFor="intake-line2" error={errors.line2?.message}>
            <Input id="intake-line2" {...form.register('line2')} />
          </IntakeField>

          <IntakeField label="Village" htmlFor="intake-city" error={errors.city?.message}>
            <Input id="intake-city" {...form.register('city')} aria-invalid={Boolean(errors.city)} />
          </IntakeField>

          <IntakeField label="Island" htmlFor="intake-region" error={errors.region?.message}>
            <Input id="intake-region" {...form.register('region')} aria-invalid={Boolean(errors.region)} />
          </IntakeField>

          <IntakeField label="Postal code" htmlFor="intake-postal" error={errors.postalCode?.message}>
            <Input id="intake-postal" {...form.register('postalCode')} />
          </IntakeField>

          <IntakeField
            label="Description"
            htmlFor="intake-description"
            error={errors.description?.message}
            hint="How a crew would find the meter."
          >
            <Input id="intake-description" {...form.register('description')} />
          </IntakeField>
        </IntakeFields>
      ) : locations.isPending ? (
        <Skeleton className="h-10 max-w-lg" />
      ) : (
        <IntakeField
          label="Existing premise"
          htmlFor="intake-location"
          error={errors.serviceLocationId?.message}
          hint={
            available.length === 0
              ? 'Every registered premise is already served. Register a new one instead.'
              : 'Only premises that are active and not already served are listed.'
          }
          className="max-w-lg"
        >
          <Select
            id="intake-location"
            fullWidth
            className="h-10 w-full text-sm"
            aria-invalid={Boolean(errors.serviceLocationId)}
            {...form.register('serviceLocationId')}
          >
            <option value="">Choose a premise…</option>
            {available.map((location) => (
              <option key={location.id} value={location.id}>
                {location.locationCode} — {location.formattedAddress}
              </option>
            ))}
          </Select>
        </IntakeField>
      )}
    </div>
  );
}
