import { zodResolver } from '@hookform/resolvers/zod';
import { useMutation } from '@tanstack/react-query';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { customersApi, type Customer, type ServiceLocation } from '@/api/customers';
import { toast } from '@/components/feedback/toast';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { StepFacts, StepField, StepFields } from './step-card';

/**
 * SPEC step 1 — Create Customer.
 *
 * Two registries, two records: the customer, and the premise they are served at. They are
 * registered together here because a demonstration should not make somebody fill in two forms to
 * reach the second step, but they remain two calls to two endpoints, and the facts below report
 * both numbers so nobody mistakes them for one record.
 */

const schema = z.object({
  name: z.string().trim().min(1, 'A customer needs a name.').max(200),
  class: z.enum(['Residential', 'Commercial']),
  contactName: z.string().trim().max(200).optional(),
  line1: z.string().trim().min(1, 'A premise needs a street address.').max(200),
  city: z.string().trim().min(1, 'A premise needs a village or town.').max(100),
  region: z.string().trim().min(1, 'A premise needs an island.').max(100),
});

type Values = z.infer<typeof schema>;

export type CustomerStepResult = { customer: Customer; location: ServiceLocation };

export function CustomerStep({
  result,
  onDone,
}: {
  result?: CustomerStepResult;
  onDone: (result: CustomerStepResult) => void;
}) {
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: {
      name: 'Reyes Family Residence',
      class: 'Residential',
      contactName: 'Ana Reyes',
      line1: '77 As Nieves Road',
      city: 'Songsong',
      region: 'Rota',
    },
  });

  const register = useMutation({
    mutationFn: async (values: Values) => {
      const customer = await customersApi.create({
        name: values.name,
        class: values.class,
        contactName: values.contactName || null,
      });

      const location = await customersApi.createLocation({
        address: {
          line1: values.line1,
          city: values.city,
          region: values.region,
          // MP — the demo world is the Marianas, and the host stores the country on the address.
          country: 'MP',
        },
        description: 'Meter on the north wall',
      });

      return { customer, location };
    },
    onSuccess: (created) => {
      toast.success(`${created.customer.name} registered`, `Customer ${created.customer.accountNumber}`);
      onDone(created);
    },
    onError: (error) => toast.apiError(error, 'The customer could not be registered.'),
  });

  if (result) {
    return (
      <StepFacts
        facts={[
          { label: 'Customer', value: result.customer.name },
          { label: 'Customer number', value: result.customer.accountNumber },
          { label: 'Status', value: result.customer.status },
          { label: 'Premise', value: result.location.formattedAddress },
          { label: 'Premise code', value: result.location.locationCode },
        ]}
      />
    );
  }

  return (
    <form onSubmit={form.handleSubmit((values) => register.mutate(values))} noValidate>
      <StepFields>
        <StepField label="Customer name" htmlFor="customer-name" error={form.formState.errors.name?.message}>
          <Input id="customer-name" {...form.register('name')} aria-invalid={Boolean(form.formState.errors.name)} />
        </StepField>

        <StepField label="Class" htmlFor="customer-class">
          <Select id="customer-class" fullWidth className="h-10 w-full text-sm" {...form.register('class')}>
            <option value="Residential">Residential</option>
            <option value="Commercial">Commercial</option>
          </Select>
        </StepField>

        <StepField label="Contact" htmlFor="customer-contact">
          <Input id="customer-contact" {...form.register('contactName')} />
        </StepField>

        <StepField label="Street address" htmlFor="premise-line1" error={form.formState.errors.line1?.message}>
          <Input id="premise-line1" {...form.register('line1')} aria-invalid={Boolean(form.formState.errors.line1)} />
        </StepField>

        <StepField label="Village" htmlFor="premise-city" error={form.formState.errors.city?.message}>
          <Input id="premise-city" {...form.register('city')} aria-invalid={Boolean(form.formState.errors.city)} />
        </StepField>

        <StepField label="Island" htmlFor="premise-region" error={form.formState.errors.region?.message}>
          <Input id="premise-region" {...form.register('region')} aria-invalid={Boolean(form.formState.errors.region)} />
        </StepField>
      </StepFields>

      <Button type="submit" className="mt-5" disabled={register.isPending}>
        {register.isPending ? 'Registering…' : 'Register customer and premise'}
      </Button>
    </form>
  );
}
