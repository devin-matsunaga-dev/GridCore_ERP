import { describe, expect, it } from 'vitest';
import type { DepositRule, ServiceAccount, ServiceLocation } from '@/api/customers';
import { serviceAccount, serviceLocation } from '@/test/registry-fixtures';
import {
  assessmentFor,
  availablePremises,
  buildIntake,
  emptyIntake,
  fieldsForStep,
  intakeSchema,
  intakeSteps,
  isWholeCents,
  parseDeposit,
  reviewFacts,
  stepStatus,
  toCents,
  type IntakeRules,
  type IntakeValues,
} from './registration';

/**
 * The intake wizard's logic, tested without rendering anything (CONVENTIONS.md ⚡). Every rule the
 * host enforces has its twin here, because a five-step form that discovers a refusal on the last
 * step is a worse product than one that says so on the step that caused it.
 */

/**
 * The schedule as the host publishes it: keyed on (class × service) since WP-2.17, so a lookup by
 * class alone would now match whichever of these the array happened to yield first.
 */
const schedule: DepositRule[] = [
  rule('Residential', 'Electricity', 75, 'd1', { usageMonths: 2, usageRate: 0.32 }),
  rule('Residential', 'Wastewater', 30, 'd2'),
  rule('Commercial', 'Electricity', 450, 'd3', { usageMonths: 2, usageRate: 0.32 }),
  rule('Commercial', 'Wastewater', 150, 'd4'),
];

function rule(
  customerClass: DepositRule['customerClass'],
  serviceType: DepositRule['serviceType'],
  amount: number,
  suffix: string,
  usage: { usageMonths: number; usageRate: number } | null = null,
): DepositRule {
  return {
    customerClass,
    serviceType,
    isMetered: serviceType !== 'Wastewater',
    amount,
    minimumAmount: amount,
    usageMonths: usage?.usageMonths ?? null,
    usageRate: usage?.usageRate ?? null,
    description: `${customerClass} ${serviceType}.`,
    ruleId: `0192f000-0000-7000-8000-00000000${suffix}00`,
  };
}

function values(overrides: Partial<IntakeValues> = {}): IntakeValues {
  return {
    ...emptyIntake,
    name: 'Reyes Family Residence',
    contactName: 'Ana Reyes',
    line1: '77 As Nieves Road',
    city: 'Songsong',
    region: 'Rota',
    ...overrides,
  };
}

/** The schedule has been read and the caller may take a deposit, unless a test says otherwise. */
function validate(intake: IntakeValues, rules: Partial<IntakeRules> = {}) {
  return intakeSchema({ assessedAmount: 75, mayCollectDeposit: true, ...rules }).safeParse(intake);
}

function messagesFor(result: ReturnType<typeof validate>, field: keyof IntakeValues): string[] {
  if (result.success) return [];

  return result.error.issues.filter((issue) => issue.path[0] === field).map((issue) => issue.message);
}

describe('the steps', () => {
  it('covers every field exactly once across the steps that collect them', () => {
    // A field on two steps would be validated twice and edited in two places; a field on none would
    // reach the host unvalidated. Review collects nothing, which is what makes it a review.
    const collected = intakeSteps.flatMap((step) => step.fields);

    expect(new Set(collected).size).toBe(collected.length);
    expect(collected.toSorted()).toEqual(Object.keys(emptyIntake).toSorted());
    expect(fieldsForStep('review')).toEqual([]);
  });

  it('marks the steps behind the current one done and the ones ahead waiting', () => {
    expect(stepStatus(2, 0)).toBe('done');
    expect(stepStatus(2, 2)).toBe('active');
    expect(stepStatus(2, 3)).toBe('waiting');
  });
});

describe('identity', () => {
  it('needs a name', () => {
    expect(messagesFor(validate(values({ name: '   ' })), 'name')).toContain('A customer needs a name.');
  });

  it('accepts an empty email but not a broken one', () => {
    expect(validate(values({ email: '' })).success).toBe(true);
    expect(messagesFor(validate(values({ email: 'ana.reyes.example.com' })), 'email')).toHaveLength(1);
    expect(validate(values({ email: 'ana.reyes@example.com' })).success).toBe(true);
  });
});

describe('the premise', () => {
  it('needs the three parts an address cannot be built without', () => {
    const result = validate(values({ line1: '', city: '', region: '' }));

    expect(messagesFor(result, 'line1')).toContain('A premise needs a street address.');
    expect(messagesFor(result, 'city')).toContain('A premise needs a village or town.');
    expect(messagesFor(result, 'region')).toContain('A premise needs an island.');
  });

  it('asks for a premise to be picked when the account is opened at an existing one', () => {
    const result = validate(values({ premiseMode: 'existing', line1: '', city: '', region: '' }));

    expect(messagesFor(result, 'serviceLocationId')).toContain('Pick the premise this account is for.');

    // The address boxes are not this mode's business, so their emptiness is not an error.
    expect(messagesFor(result, 'line1')).toEqual([]);
  });

  it('is satisfied by a picked premise', () => {
    expect(
      validate(values({ premiseMode: 'existing', serviceLocationId: serviceLocation().id })).success,
    ).toBe(true);
  });

  it('offers only premises that are active and not already served', () => {
    const served = serviceLocation({ id: 'served', locationCode: 'L-000002' });
    const retired = serviceLocation({ id: 'retired', locationCode: 'L-000003', isActive: false });
    const free = serviceLocation({ id: 'free', locationCode: 'L-000004' });

    const accounts: ServiceAccount[] = [
      serviceAccount({ id: 'a1', serviceLocationId: 'served', status: 'Active' }),
      // A closed account releases its premise — that is the whole point of closing one.
      serviceAccount({ id: 'a2', serviceLocationId: 'free', status: 'Closed' }),
    ];

    const offered = availablePremises([served, retired, free] as ServiceLocation[], accounts, 'Electricity');

    expect(offered.map((location) => location.id)).toEqual(['free']);
  });

  it('still offers a premise that takes a different supply', () => {
    // WP-2.17's shape. A house already on electricity may take water and wastewater as well, and
    // hiding it would make the three-supply premise unreachable from the wizard that opens accounts.
    const premise = serviceLocation({ id: 'served', locationCode: 'L-000002' });

    const accounts: ServiceAccount[] = [
      serviceAccount({ id: 'a1', serviceLocationId: 'served', status: 'Active', serviceType: 'Electricity' }),
    ];

    expect(availablePremises([premise] as ServiceLocation[], accounts, 'Water').map((l) => l.id)).toEqual(['served']);
    expect(availablePremises([premise] as ServiceLocation[], accounts, 'Electricity')).toEqual([]);
  });

  it('offers nothing rather than throwing while the registries are still loading', () => {
    expect(availablePremises(undefined, undefined, 'Electricity')).toEqual([]);
  });
});

describe('the deposit', () => {
  it('assesses from the schedule the host published, per class AND service', () => {
    expect(assessmentFor(schedule, 'Residential', 'Electricity')?.amount).toBe(75);
    expect(assessmentFor(schedule, 'Commercial', 'Electricity')?.amount).toBe(450);

    // The half WP-2.17 added. A lookup by class alone would have matched the electric rule for both.
    expect(assessmentFor(schedule, 'Residential', 'Wastewater')?.amount).toBe(30);
    expect(assessmentFor(schedule, 'Commercial', 'Wastewater')?.amount).toBe(150);

    expect(assessmentFor(undefined, 'Residential', 'Electricity')).toBeUndefined();

    // A pair the schedule does not cover reads as "still loading", not as zero.
    expect(assessmentFor(schedule, 'Residential', 'Gas')).toBeUndefined();
  });

  it('reads an empty box as nothing collected rather than as NaN', () => {
    expect(parseDeposit('')).toBe(0);
    expect(parseDeposit('  ')).toBe(0);
    expect(parseDeposit('75.00')).toBe(75);
  });

  it('compares in cents, because these are decimals on the wire and floats in the browser', () => {
    expect(toCents(0.1 + 0.2)).toBe(30);
    expect(isWholeCents(0.1 + 0.2)).toBe(true);
    expect(isWholeCents(75.125)).toBe(false);
  });

  it('allows part of the assessed amount', () => {
    expect(validate(values({ depositCollected: '25.00' })).success).toBe(true);
  });

  it('refuses more than the schedule asks for, and says what it asks for', () => {
    expect(messagesFor(validate(values({ depositCollected: '500' })), 'depositCollected')).toEqual([
      'The schedule asks 75.00 for a residential electricity account. Collect that or less.',
    ]);
  });

  it('refuses an amount finer than a cent rather than rounding it', () => {
    expect(messagesFor(validate(values({ depositCollected: '75.125' })), 'depositCollected')).toEqual([
      'A deposit must be a whole number of cents.',
    ]);
  });

  it('refuses a negative amount', () => {
    expect(messagesFor(validate(values({ depositCollected: '-1' })), 'depositCollected')).toEqual([
      'A deposit collected cannot be negative.',
    ]);
  });

  it('refuses anything that is not an amount of money', () => {
    expect(messagesFor(validate(values({ depositCollected: 'waived' })), 'depositCollected')).toEqual([
      'A deposit is an amount of money.',
    ]);
  });

  it('refuses a collection by somebody without the permission, and allows a waived one', () => {
    // The same refusal the host makes (403), made before the request rather than after it.
    expect(
      messagesFor(validate(values({ depositCollected: '75' }), { mayCollectDeposit: false }), 'depositCollected'),
    ).toEqual([
      'You do not hold the permission to collect a deposit. Register without one and ask somebody who does.',
    ]);

    expect(validate(values({ depositCollected: '' }), { mayCollectDeposit: false }).success).toBe(true);
  });

  it('does not check against a schedule it has not read yet', () => {
    // The rules query is still in flight. Refusing every figure until it lands would be worse than
    // letting the host have the last word, which it has either way.
    expect(validate(values({ depositCollected: '9999' }), { assessedAmount: undefined }).success).toBe(true);
  });
});

describe('the request the form becomes', () => {
  it('is one intake carrying a new premise', () => {
    const intake = buildIntake(
      values({ line2: '', postalCode: '96951', description: 'Meter on the north wall', depositCollected: '75' }),
    );

    expect(intake).toEqual({
      name: 'Reyes Family Residence',
      class: 'Residential',
      contactName: 'Ana Reyes',
      email: null,
      phone: null,
      premise: {
        newPremise: {
          address: {
            line1: '77 As Nieves Road',
            line2: null,
            city: 'Songsong',
            region: 'Rota',
            postalCode: '96951',
            country: 'MP',
          },
          description: 'Meter on the north wall',
        },
      },
      serviceType: 'Electricity',
      depositCollected: 75,
      startService: true,
      reason: 'Requested at the counter',
    });
  });

  it('carries only the premise id when the account is opened at an existing one', () => {
    const intake = buildIntake(values({ premiseMode: 'existing', serviceLocationId: 'L1' }));

    expect(intake.premise).toEqual({ serviceLocationId: 'L1' });
  });

  it('sends null rather than an empty string for an optional box left blank', () => {
    // An empty string is a value, and the host would store it as the contact's name.
    const intake = buildIntake(values({ contactName: '  ', reason: '' }));

    expect(intake.contactName).toBeNull();
    expect(intake.reason).toBeNull();
  });

  it('sends nothing collected as zero, not as an empty string', () => {
    expect(buildIntake(values({ depositCollected: '' })).depositCollected).toBe(0);
  });
});

describe('the review', () => {
  it('summarises what will be written, including the deposit assessed beside the one taken', () => {
    const facts = reviewFacts(values({ depositCollected: '25' }), schedule[0], undefined);

    expect(facts.find((fact) => fact.label === 'Deposit assessed')?.value).toBe('75.00');
    expect(facts.find((fact) => fact.label === 'Deposit collected')?.value).toBe('25.00');
    expect(facts.find((fact) => fact.label === 'New premise')?.value).toBe('77 As Nieves Road, Songsong, Rota');
    expect(facts.find((fact) => fact.label === 'Supply')?.value).toBe('Energised on registration');
  });

  it('names the premise picked when the account is opened at an existing one', () => {
    const facts = reviewFacts(
      values({ premiseMode: 'existing', serviceLocationId: 'L1', startService: false }),
      schedule[0],
      '12 Songsong Village Road, Songsong, Rota',
    );

    expect(facts.find((fact) => fact.label === 'Existing premise')?.value).toBe(
      '12 Songsong Village Road, Songsong, Rota',
    );
    expect(facts.find((fact) => fact.label === 'Supply')?.value).toBe('Account opened, not energised');
  });

  it('shows an em dash for a contact nobody gave', () => {
    expect(reviewFacts(values({ contactName: '' }), schedule[0], undefined)
      .find((fact) => fact.label === 'Contact')?.value).toBe('—');
  });
});
