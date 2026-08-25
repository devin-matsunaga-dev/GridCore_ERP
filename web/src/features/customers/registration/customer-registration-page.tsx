import { zodResolver } from '@hookform/resolvers/zod';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { ArrowLeft, ArrowRight, Check, X } from 'lucide-react';
import { useCallback, useState } from 'react';
import { useForm, useWatch, type Resolver } from 'react-hook-form';
import { useNavigate } from 'react-router';
import {
  customerKeys,
  customersApi,
  useDepositRules,
  useServiceLocations,
  type CustomerRegistration,
} from '@/api/customers';
import { useCurrentUser } from '@/api/identity';
import { toast } from '@/components/feedback/toast';
import { PageHeader } from '@/components/registry/page-header';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import { formatCount, formatMoney } from '@/lib/format';
import { cn } from '@/lib/utils';
import { DepositStep } from './components/deposit-step';
import { IdentityStep } from './components/identity-step';
import { PremiseStep } from './components/premise-step';
import { ReviewStep } from './components/review-step';
import { ServiceStep } from './components/service-step';
import {
  assessmentFor,
  buildIntake,
  emptyIntake,
  fieldsForStep,
  intakeSchema,
  intakeSteps,
  stepStatus,
  type IntakeValues,
} from './registration';

/** The permission the deposit step is gated on — `Permissions.Customers.Deposit` on the host. */
const depositPermission = 'customers.deposit';

/**
 * Customer intake: one guided flow, not eight screens.
 *
 * Identity and contacts → the premise → the service account → the deposit → review. Per-step
 * validation, back and forward without losing a keystroke, and **one commit at the end**: the
 * customer, the premise and the account are written in a single host-side transaction, so a wizard
 * abandoned half-way leaves nothing behind. That is why this is one request rather than the three
 * calls the demonstration walk makes deliberately visible.
 *
 * The deposit is assessed from the published schedule rather than typed, and collecting one needs
 * `customers.deposit` — a narrower grant than the `customers.write` that opened the wizard.
 */
export function CustomerRegistrationPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  const [index, setIndex] = useState(0);
  const step = intakeSteps[index];

  const rules = useDepositRules();
  const me = useCurrentUser();

  // False while `/api/me` is in flight, which reads as "not yet" rather than "not allowed": the
  // deposit step shows its skeleton until the schedule and the caller are both known.
  const mayCollectDeposit = me.data?.permissions.includes(depositPermission) ?? false;

  // Two of the form's rules are not knowable up front: what the schedule asks of the class chosen,
  // and whether this caller may take a deposit at all. Both arrive from the API, so the assessed
  // figure is derived inside the resolver from the values being validated rather than captured when
  // the form was created. The wizard then refuses exactly what the host would refuse, which is the
  // point — a five-step form that discovers its answer on the last step has wasted an afternoon.
  const resolver = useCallback<Resolver<IntakeValues>>(
    (values, context, options) =>
      zodResolver(
        intakeSchema({
          assessedAmount: assessmentFor(rules.data, values.class)?.amount,
          mayCollectDeposit,
        }),
      )(values, context, options),
    [rules.data, mayCollectDeposit],
  );

  const form = useForm<IntakeValues>({ resolver, defaultValues: emptyIntake, mode: 'onTouched' });

  // Subscribes the page to every field, which is what lets the assessed figure and the review list
  // follow the form as it is typed. `useWatch` rather than `form.watch()`: the latter returns a new
  // object on every render, which React Compiler cannot memoize past.
  const values = useWatch({ control: form.control, defaultValue: emptyIntake }) as IntakeValues;

  const assessment = assessmentFor(rules.data, values.class);

  const premises = useServiceLocations({ isActive: true });

  const premiseLabel = premises.data?.find((location) => location.id === values.serviceLocationId)
    ?.formattedAddress;

  const register = useMutation({
    mutationFn: (intake: IntakeValues) => customersApi.register(buildIntake(intake)),
    onSuccess: (registration: CustomerRegistration) => {
      toast.success(
        `${registration.customer.name} registered`,
        [
          `Customer ${registration.customer.accountNumber}`,
          `account ${registration.account.accountNumber}`,
          registration.deposit.collectedAmount > 0
            ? `deposit ${formatMoney(registration.deposit.collectedAmount)}`
            : 'no deposit taken',
        ].join(' · '),
      );

      // The registry, the premise list and the account list are all a row heavier now.
      void queryClient.invalidateQueries({ queryKey: customerKeys.all });
      void queryClient.invalidateQueries({ queryKey: ['service-locations'] });
      void queryClient.invalidateQueries({ queryKey: ['service-accounts'] });

      void navigate(`/customers/${registration.customer.id}`);
    },
    onError: (error) => toast.apiError(error, 'The customer could not be registered.'),
  });

  const isLast = index === intakeSteps.length - 1;

  async function goForward() {
    // Only this step's own fields: a later step's empty box is not this step's problem, which is
    // what makes the wizard walkable rather than a single form pretending to be five.
    if (!(await form.trigger(fieldsForStep(step.id)))) return;

    setIndex((current) => Math.min(current + 1, intakeSteps.length - 1));
  }

  return (
    <div className="space-y-6">
      <PageHeader
        title="Register a customer"
        subtitle="Identity, premise, service account and deposit — one flow, committed once at the end."
        actions={
          <Button variant="secondary" onClick={() => void navigate('/customers')}>
            <X aria-hidden="true" />
            Cancel
          </Button>
        }
      />

      <Card className="px-6 py-4">
        <div className="flex flex-wrap items-center justify-between gap-x-6 gap-y-3">
          <ol className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-2">
            {intakeSteps.map((candidate, position) => {
              const status = stepStatus(index, position);

              return (
                <li key={candidate.id} className="flex items-center gap-2">
                  <span
                    className={cn(
                      'flex size-6 items-center justify-center rounded-full text-xs font-semibold tabular-nums',
                      status === 'done' && 'bg-success text-white',
                      status === 'active' && 'bg-primary text-primary-foreground',
                      status === 'waiting' && 'bg-neutral-soft text-neutral',
                    )}
                    aria-hidden="true"
                  >
                    {status === 'done' ? <Check className="size-3.5" strokeWidth={2.5} /> : position + 1}
                  </span>
                  <span
                    className={cn(
                      'text-[13px]',
                      status === 'waiting' ? 'text-muted' : 'text-heading font-medium',
                    )}
                  >
                    {candidate.title}
                  </span>
                  {position < intakeSteps.length - 1 && (
                    <span className="text-muted mx-1 hidden text-xs lg:inline" aria-hidden="true">
                      ›
                    </span>
                  )}
                </li>
              );
            })}
          </ol>

          <p className="text-muted tabular shrink-0 text-[13px]">
            Step {formatCount(index + 1)} of {formatCount(intakeSteps.length)}
          </p>
        </div>
      </Card>

      <Card>
        <form
          onSubmit={form.handleSubmit((submitted) => register.mutate(submitted))}
          noValidate
          aria-current="step"
        >
          <div className="px-6 pt-5 pb-5">
            <h3 className="text-heading text-base leading-tight font-semibold">{step.title}</h3>
            <p className="text-muted mt-1 text-[13px]">{step.summary}</p>

            <div className="mt-5">
              {step.id === 'identity' && <IdentityStep form={form} />}
              {step.id === 'premise' && <PremiseStep form={form} />}
              {step.id === 'service' && <ServiceStep form={form} />}
              {step.id === 'deposit' && (
                <DepositStep
                  form={form}
                  assessment={assessment}
                  isLoading={rules.isPending || me.isPending}
                  mayCollect={mayCollectDeposit}
                />
              )}
              {step.id === 'review' && (
                <ReviewStep values={values} assessment={assessment} premiseLabel={premiseLabel} />
              )}
            </div>
          </div>

          <div className="border-border flex items-center justify-between gap-3 border-t px-6 py-4">
            <Button
              type="button"
              variant="secondary"
              onClick={() => setIndex((current) => Math.max(current - 1, 0))}
              disabled={index === 0 || register.isPending}
            >
              <ArrowLeft aria-hidden="true" />
              Back
            </Button>

            {isLast ? (
              <Button type="submit" disabled={register.isPending}>
                {register.isPending ? 'Registering…' : 'Register customer'}
              </Button>
            ) : (
              <Button type="button" onClick={() => void goForward()}>
                Next
                <ArrowRight aria-hidden="true" />
              </Button>
            )}
          </div>
        </form>
      </Card>
    </div>
  );
}
