import type { UseFormReturn } from 'react-hook-form';
import { Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import type { IntakeValues } from '../registration';
import { IntakeField, IntakeFields } from './intake-field';

/**
 * Step 3 — the service account.
 *
 * There is nothing to name here: the account number is the registry's to issue, and the customer
 * and premise are already chosen. What is left is the one decision — whether supply is energised
 * now or the account is left open and unenergised until a crew has been.
 *
 * Energising is a genuinely separate act with its own history line and its own event, and the
 * billing run refuses an account that was never energised by name. Both happen inside the intake's
 * one transaction, so choosing "yes" here is not a second commit.
 */
export function ServiceStep({ form }: { form: UseFormReturn<IntakeValues> }) {
  const { errors } = form.formState;

  return (
    <IntakeFields>
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
