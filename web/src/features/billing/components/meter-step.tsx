import { zodResolver } from '@hookform/resolvers/zod';
import { useMutation } from '@tanstack/react-query';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import type { ServiceLocation } from '@/api/customers';
import { meterTypes, meteringApi, type Meter } from '@/api/metering';
import { toast } from '@/components/feedback/toast';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { formatLabel, formatQuantity } from '@/lib/format';
import { StepFacts, StepField, StepFields } from './step-card';

/**
 * SPEC step 3 — Assign Meter.
 *
 * The meter is fitted to the **premise**, never to the account: there is no account field on this
 * form and there must not be one. The bill resolves the account as "the one open at the premise
 * this meter is on", which is the derivation the next three steps depend on.
 *
 * The installation reading is what the first period is measured from — not zero, or the customer
 * would be billed for every unit the meter had ever counted.
 */

const schema = z.object({
  serialNumber: z.string().trim().min(1, 'A meter needs its manufacturer’s serial number.').max(64),
  type: z.enum(meterTypes),
  installationReading: z.coerce
    .number({ message: 'The dials read a number.' })
    .min(0, 'A reading cannot be negative.')
    // Refused rather than rounded, exactly as the host refuses it: rounding is for figures GridCore
    // computes, refusal is for figures somebody typed.
    .refine((value) => Number.isInteger(Math.round(value * 1000)) && Math.abs(value * 1000 - Math.round(value * 1000)) < 1e-9, {
      message: 'A meter reading is stored to three decimal places.',
    }),
});

type Values = z.input<typeof schema>;

/** A serial number nothing in the register can already be using. */
function suggestSerial(): string {
  return `SEN-${Math.floor(Math.random() * 900_000 + 100_000)}`;
}

export function MeterStep({
  location,
  result,
  onDone,
}: {
  location: ServiceLocation;
  result?: Meter;
  onDone: (meter: Meter) => void;
}) {
  const form = useForm({
    resolver: zodResolver(schema),
    defaultValues: { serialNumber: suggestSerial(), type: 'SinglePhase', installationReading: 4200 } as Values,
  });

  const fit = useMutation({
    mutationFn: async (values: z.output<typeof schema>) => {
      const registered = await meteringApi.register({
        serialNumber: values.serialNumber,
        type: values.type,
        manufacturer: 'Sensus',
        note: 'New connection',
      });

      return meteringApi.assign(registered.id, {
        serviceLocationId: location.id,
        installationReading: values.installationReading,
        note: 'Meter set on the north wall',
      });
    },
    onSuccess: (meter) => {
      toast.success(`Meter ${meter.meterNumber} fitted`, location.formattedAddress);
      onDone(meter);
    },
    onError: (error) => toast.apiError(error, 'The meter could not be fitted.'),
  });

  if (result) {
    return (
      <StepFacts
        facts={[
          { label: 'Meter number', value: result.meterNumber },
          { label: 'Serial', value: result.serialNumber },
          { label: 'Type', value: formatLabel(result.type) },
          { label: 'Status', value: result.status },
          {
            label: 'Installation reading',
            value: result.installationReading === null ? null : formatQuantity(result.installationReading),
            numeric: true,
          },
          { label: 'Register', value: `${result.registerDigits} digits`, numeric: true },
        ]}
      />
    );
  }

  return (
    <form onSubmit={form.handleSubmit((values) => fit.mutate(values))} noValidate>
      <StepFields>
        <StepField label="Serial number" htmlFor="meter-serial" error={form.formState.errors.serialNumber?.message}>
          <Input
            id="meter-serial"
            {...form.register('serialNumber')}
            aria-invalid={Boolean(form.formState.errors.serialNumber)}
          />
        </StepField>

        <StepField label="Type" htmlFor="meter-type">
          <Select id="meter-type" fullWidth className="h-10 w-full text-sm" {...form.register('type')}>
            {meterTypes.map((type) => (
              <option key={type} value={type}>
                {formatLabel(type)}
              </option>
            ))}
          </Select>
        </StepField>

        <StepField
          label="Installation reading"
          htmlFor="meter-installation-reading"
          error={form.formState.errors.installationReading?.message}
          hint="What the dials read as it went on — the first period is measured from here, not zero."
        >
          <Input
            id="meter-installation-reading"
            type="number"
            step="0.001"
            min="0"
            {...form.register('installationReading')}
            aria-invalid={Boolean(form.formState.errors.installationReading)}
          />
        </StepField>
      </StepFields>

      <Button type="submit" className="mt-5" disabled={fit.isPending}>
        {fit.isPending ? 'Fitting…' : 'Register and fit the meter'}
      </Button>
    </form>
  );
}
