import { zodResolver } from '@hookform/resolvers/zod';
import { useMutation } from '@tanstack/react-query';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { meteringApi, type Meter, type MeterReading, type ReadingCycle } from '@/api/metering';
import { toast } from '@/components/feedback/toast';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { StatusPill } from '@/components/ui/status';
import { formatCount, formatLabel, formatQuantity } from '@/lib/format';
import { StepFacts, StepField, StepFields } from './step-card';

/**
 * SPEC steps 4 and 5 — Generate Simulated Reading, Calculate Consumption.
 *
 * The batch goes through `IMeterReadingProvider`, which is the product's external boundary: no
 * domain code calls the simulator directly, and production swaps the registration for a real head
 * end without touching anything here (invariant 6).
 *
 * A cycle reads **every fitted meter**, because that is what a utility's reading run is. There is
 * no per-premise form of it and inventing one for a demonstration would be demonstrating a
 * different product — so the whole batch's figures are reported, and this premise's reading is
 * picked out of it.
 *
 * Consumption is not a second button. Metering works out what a reading means the moment it lands:
 * a provider reads meters and never decides what a reading means.
 */

const schema = z.object({
  cycleCode: z.string().trim().min(1, 'A cycle needs a code.').max(32),
});

type Values = z.infer<typeof schema>;

export type ReadingStepResult = { cycle: ReadingCycle; reading: MeterReading };

export function ReadingStep({
  meter,
  defaultCycleCode,
  result,
  onDone,
}: {
  meter: Meter;
  defaultCycleCode: string;
  result?: ReadingStepResult;
  onDone: (result: ReadingStepResult) => void;
}) {
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: { cycleCode: defaultCycleCode },
  });

  const run = useMutation({
    mutationFn: async (values: Values) => {
      const cycle = await meteringApi.runCycle({ cycleCode: values.cycleCode, seed: 4471 });
      const reading = cycle.readings.find((candidate) => candidate.meterId === meter.id);

      if (!reading) {
        // Defensive, and worth saying out loud: the cycle reads every fitted meter, so a meter
        // fitted a moment ago that produced no reading means the batch did not cover it, and
        // carrying on would bill a period nothing measured.
        throw new Error(`The cycle recorded no reading for meter ${meter.meterNumber}.`);
      }

      return { cycle, reading };
    },
    onSuccess: (produced) => {
      toast.success(
        `${formatCount(produced.cycle.recorded)} meters read`,
        `Cycle ${produced.cycle.cycleCode} · ${formatCount(produced.cycle.exceptions)} on the worklist`,
      );
      onDone(produced);
    },
    onError: (error) => toast.apiError(error, 'The reading cycle could not be run.'),
  });

  if (result) {
    const { cycle, reading } = result;

    return (
      <div className="space-y-4">
        <StepFacts
          facts={[
            { label: 'Cycle', value: cycle.cycleCode },
            { label: 'Provider', value: cycle.provider },
            { label: 'Meters read', value: formatCount(cycle.recorded), numeric: true },
            { label: 'On the worklist', value: formatCount(cycle.exceptions), numeric: true },
            { label: 'Seed', value: formatCount(cycle.seed), numeric: true },
          ]}
        />

        <div className="border-border border-t pt-4">
          <p className="text-muted mb-3 text-[13px] font-medium">This premise’s reading</p>
          <StepFacts
            facts={[
              {
                label: 'Previous reading',
                value: reading.previousReading === null ? null : formatQuantity(reading.previousReading),
                numeric: true,
              },
              {
                label: 'This reading',
                value: reading.reading === null ? null : formatQuantity(reading.reading),
                numeric: true,
              },
              {
                label: 'Consumption',
                value:
                  reading.consumption === null
                    ? null
                    : `${formatQuantity(reading.consumption)} ${meterUnit}`,
                numeric: true,
              },
              { label: 'Days', value: reading.days === null ? null : formatCount(reading.days), numeric: true },
              {
                label: 'Per day',
                value: reading.dailyConsumption === null ? null : formatQuantity(reading.dailyConsumption),
                numeric: true,
              },
              {
                label: 'Exception',
                value: reading.isException ? (
                  <StatusPill status={formatLabel(reading.exceptionCode)} tone="warning" />
                ) : (
                  'None'
                ),
              },
            ]}
          />
          {reading.isException && (
            <p className="text-warning mt-3 text-[13px]">
              A flagged reading is worked by hand before it becomes a bill, so the billing run will
              skip it and name the reason.
            </p>
          )}
        </div>
      </div>
    );
  }

  return (
    <form onSubmit={form.handleSubmit((values) => run.mutate(values))} noValidate>
      <StepFields>
        <StepField
          label="Cycle code"
          htmlFor="reading-cycle-code"
          error={form.formState.errors.cycleCode?.message}
          hint="The idempotency key: running one twice is refused, so each demonstration takes a fresh code."
        >
          <Input
            id="reading-cycle-code"
            {...form.register('cycleCode')}
            aria-invalid={Boolean(form.formState.errors.cycleCode)}
          />
        </StepField>
      </StepFields>

      <Button type="submit" className="mt-5" disabled={run.isPending}>
        {run.isPending ? 'Reading meters…' : 'Run the reading cycle'}
      </Button>
    </form>
  );
}

/** What the readings are counted in. The bill carries the authoritative unit; this labels a figure. */
const meterUnit = 'kWh';
