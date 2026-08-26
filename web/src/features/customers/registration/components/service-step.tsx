import type { UseFormReturn } from 'react-hook-form';
import { isMeteredService, serviceTypeLabel } from '@/api/customers';
import { Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { intakeServiceTypes, type IntakeValues } from '../registration';
import { IntakeField, IntakeFields } from './intake-field';

/**
 * Step 3 — the service account.
 *
 * There is nothing to name here: the account number is the registry's to issue, and the customer
 * and premise are already chosen. What is left is two decisions — WHICH SUPPLY the account is for
 * (WP-2.17), and whether it is energised now or left open and unenergised until a crew has been.
 *
 * The supply comes first because everything after it follows: the deposit is assessed from the class
 * and the service together, an unmetered account is never billed from a meter reading, and the
 * premise picker on the step before narrows to premises not already taking that supply.
 *
 * Energising is a genuinely separate act with its own history line and its own event, and the
 * billing run refuses an account that was never energised by name. Both happen inside the intake's
 * one transaction, so choosing "yes" here is not a second commit.
 */
export function ServiceStep({ form }: { form: UseFormReturn<IntakeValues> }) {
  const { errors } = form.formState;
  const serviceType = form.watch('serviceType');

  return (
    <IntakeFields>
      <IntakeField
        label="Service"
        htmlFor="intake-service-type"
        error={errors.serviceType?.message}
        hint={
          isMeteredService(serviceType)
            ? 'A metered supply. The deposit is assessed from this and the class together.'
            : 'An unmetered supply — no meter is fitted and no reading is taken, so it is billed a flat charge.'
        }
      >
        <Select id="intake-service-type" fullWidth className="h-10 w-full text-sm" {...form.register('serviceType')}>
          {intakeServiceTypes.map((option) => (
            <option key={option} value={option}>
              {serviceTypeLabel(option)}
            </option>
          ))}
        </Select>
      </IntakeField>

      <IntakeField
        label="Supply"
        htmlFor="intake-start-service"
        hint="An account that is never energised cannot be billed — nothing was supplied under it."
      >
        <Select
          id="intake-start-service"
          fullWidth
          className="h-10 w-full text-sm"
          value={form.watch('startService') ? 'yes' : 'no'}
          onChange={(event) => form.setValue('startService', event.target.value === 'yes')}
        >
          <option value="yes">Energise on registration</option>
          <option value="no">Open the account only</option>
        </Select>
      </IntakeField>

      <IntakeField
        label="Reason"
        htmlFor="intake-reason"
        error={errors.reason?.message}
        hint="Recorded on the account's service history."
        className="sm:col-span-2"
      >
        <Input id="intake-reason" {...form.register('reason')} />
      </IntakeField>
    </IntakeFields>
  );
}
