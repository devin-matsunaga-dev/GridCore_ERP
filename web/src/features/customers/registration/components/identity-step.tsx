import type { UseFormReturn } from 'react-hook-form';
import { customerClasses } from '@/api/customers';
import { Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import type { IntakeValues } from '../registration';
import { IntakeField, IntakeFields } from './intake-field';

/**
 * Step 1 — who the customer is.
 *
 * The class is here rather than on the deposit step even though the deposit is assessed from it:
 * the class is a fact about the customer, and a clerk who had to go back two steps to change what a
 * deposit was assessed from would be being asked to think about the form instead of the caller.
 */
export function IdentityStep({ form }: { form: UseFormReturn<IntakeValues> }) {
  const { errors } = form.formState;

  return (
    <IntakeFields>
      <IntakeField label="Customer name" htmlFor="intake-name" error={errors.name?.message}>
        <Input id="intake-name" {...form.register('name')} aria-invalid={Boolean(errors.name)} />
      </IntakeField>

      <IntakeField
        label="Class"
        htmlFor="intake-class"
        hint="What the tariff and the deposit schedule both follow."
      >
        <Select id="intake-class" fullWidth className="h-10 w-full text-sm" {...form.register('class')}>
          {customerClasses.map((customerClass) => (
            <option key={customerClass} value={customerClass}>
              {customerClass}
            </option>
          ))}
        </Select>
      </IntakeField>

      <IntakeField
        label="Contact"
        htmlFor="intake-contact"
        error={errors.contactName?.message}
        hint="Who to ask for, where the customer is an organisation."
      >
        <Input id="intake-contact" {...form.register('contactName')} />
      </IntakeField>

      <IntakeField label="Email" htmlFor="intake-email" error={errors.email?.message}>
        <Input
          id="intake-email"
          type="email"
          {...form.register('email')}
          aria-invalid={Boolean(errors.email)}
        />
      </IntakeField>

      <IntakeField label="Phone" htmlFor="intake-phone" error={errors.phone?.message}>
        <Input id="intake-phone" {...form.register('phone')} />
      </IntakeField>
    </IntakeFields>
  );
}
