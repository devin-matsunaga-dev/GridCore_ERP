import { z } from 'zod';
import type {
  CustomerIntakeInput,
  DepositRule,
  ServiceAccount,
  ServiceLocation,
  ServiceType,
} from '@/api/customers';
import { customerClasses, isMeteredService, serviceTypeLabel, serviceTypes } from '@/api/customers';
import { isWholeCents, toCents } from '@/lib/money';

/**
 * The intake wizard's logic: which step comes next, what each one validates, what the deposit
 * schedule says, and the single request the whole form finally becomes.
 *
 * Pure, and deliberately so — no React, no DOM, no network. The wizard's rules can then be tested
 * exhaustively in milliseconds (CONVENTIONS.md ⚡), the same call `revenue-cycle.ts` made for the
 * demonstration walk. The page renders it and nothing more.
 */

/** The steps of the intake, in order. */
export const intakeStepIds = ['identity', 'premise', 'service', 'deposit', 'review'] as const;

export type IntakeStepId = (typeof intakeStepIds)[number];

/** Every field the form holds. One flat shape, so back and forward lose nothing. */
export type IntakeValues = {
  name: string;
  class: (typeof customerClasses)[number];
  contactName: string;
  email: string;
  phone: string;
  /** Register a premise as part of the intake, or open the account at one already on the books. */
  premiseMode: 'new' | 'existing';
  line1: string;
  line2: string;
  city: string;
  region: string;
  postalCode: string;
  description: string;
  serviceLocationId: string;
  /** Which supply the customer is applying for (WP-2.17) — half the deposit's key. */
  serviceType: ServiceType;
  /** Energise supply as part of the same commit. */
  startService: boolean;
  reason: string;
  /** What was taken at the counter, as typed — an empty box is nothing collected, not `NaN`. */
  depositCollected: string;
};

export type IntakeFieldName = keyof IntakeValues;

export type IntakeStep = {
  id: IntakeStepId;
  title: string;
  /** What the step is for, in one line. */
  summary: string;
  /**
   * The fields this step is answerable for. Moving forward validates exactly these — which is what
   * "per-step validation" means, and why a later step's empty box does not block an earlier one.
   */
  fields: IntakeFieldName[];
};

export const intakeSteps: IntakeStep[] = [
  {
    id: 'identity',
    title: 'Identity and contacts',
    summary: 'Who the customer is, how to reach them, and the class their tariff and deposit follow.',
    fields: ['name', 'class', 'contactName', 'email', 'phone'],
  },
  {
    id: 'premise',
    title: 'Service location',
    summary: 'Where supply is delivered. Register the premise, or open the account at one already on the books.',
    fields: ['premiseMode', 'line1', 'line2', 'city', 'region', 'postalCode', 'description', 'serviceLocationId'],
  },
  {
    id: 'service',
    title: 'Service account',
    summary: 'The account joining the two: which supply is taken, and whether it is energised now.',
    fields: ['serviceType', 'startService', 'reason'],
  },
  {
    id: 'deposit',
    title: 'Deposit',
    summary: 'Assessed from the class AND the supply against the published schedule, and collected only by somebody permitted to.',
    fields: ['depositCollected'],
  },
  {
    id: 'review',
    title: 'Review and register',
    summary: 'Everything above, in one transaction. Nothing has been written until this is pressed.',
    fields: [],
  },
];

/** The empty form. Every field is present from the start, which is what makes back and forward free. */
export const emptyIntake: IntakeValues = {
  name: '',
  class: 'Residential',
  contactName: '',
  email: '',
  phone: '',
  premiseMode: 'new',
  line1: '',
  line2: '',
  city: '',
  region: '',
  postalCode: '',
  description: '',
  serviceLocationId: '',
  serviceType: 'Electricity',
  startService: true,
  reason: 'Requested at the counter',
  depositCollected: '',
};

/** The fields a step is answerable for — what moving forward validates. */
export function fieldsForStep(id: IntakeStepId): IntakeFieldName[] {
  return intakeSteps.find((step) => step.id === id)?.fields ?? [];
}

export type IntakeStepStatus = 'done' | 'active' | 'waiting';

export function stepStatus(currentIndex: number, index: number): IntakeStepStatus {
  if (index < currentIndex) return 'done';

  return index === currentIndex ? 'active' : 'waiting';
}

/**
 * What the schedule asks of a class on a service, or `undefined` while the schedule is loading.
 *
 * Keyed on the PAIR since WP-2.17. A lookup by class alone would now match whichever of four rules
 * the host happened to return first — the residential electric deposit and the residential
 * wastewater one are different figures, and the wizard has to quote the one being applied for.
 */
export function assessmentFor(
  rules: readonly DepositRule[] | undefined,
  customerClass: IntakeValues['class'],
  serviceType: ServiceType,
): DepositRule | undefined {
  return rules?.find((rule) => rule.customerClass === customerClass && rule.serviceType === serviceType);
}

/** The supplies the wizard offers, in the enum's order. */
export const intakeServiceTypes = serviceTypes;

/**
 * Money as a whole number of cents, and whether what somebody typed is exact to one.
 *
 * Both live in `lib/money.ts` now — WP-2.10's balance arithmetic was the third caller, which is
 * when this codebase promotes a copy. Re-exported rather than moved, so every call site and every
 * test that imported them from here still does.
 */
export { isWholeCents, toCents };

/** What a typed deposit box means. An empty box is nothing collected, never `NaN`. */
export function parseDeposit(typed: string): number {
  const trimmed = typed.trim();

  return trimmed === '' ? 0 : Number(trimmed);
}

export type IntakeRules = {
  /** What the schedule asks of the class chosen; `undefined` while it loads. */
  assessedAmount: number | undefined;
  /** Whether the signed-in caller holds `customers.deposit`. */
  mayCollectDeposit: boolean;
};

/**
 * The form's rules, as the host would apply them.
 *
 * Built per render rather than declared once, because two of the rules depend on facts that arrive
 * from the API: what the schedule asks of the chosen class, and whether this caller may take a
 * deposit at all. The wizard refusing what the host would refuse is the point — a five-step form
 * that discovers its answer on the last step has wasted somebody's afternoon.
 */
export function intakeSchema({ assessedAmount, mayCollectDeposit }: IntakeRules) {
  return z
    .object({
      name: z.string().trim().min(1, 'A customer needs a name.').max(256),
      class: z.enum(customerClasses),
      contactName: z.string().trim().max(256),
      email: z.union([z.literal(''), z.email('That is not an email address.').max(320)]),
      phone: z.string().trim().max(32),
      premiseMode: z.enum(['new', 'existing']),
      line1: z.string().trim().max(200),
      line2: z.string().trim().max(200),
      city: z.string().trim().max(128),
      region: z.string().trim().max(128),
      postalCode: z.string().trim().max(16),
      description: z.string().trim().max(256),
      serviceLocationId: z.string().trim(),
      serviceType: z.enum(serviceTypes),
      startService: z.boolean(),
      reason: z.string().trim().max(512),
      depositCollected: z.string(),
    })
    .superRefine((values, context) => {
      if (values.premiseMode === 'new') {
        // The three parts the host's `Address.Create` requires. Line 2 and a postal code are
        // optional in the territory, so they are optional here.
        for (const [field, message] of [
          ['line1', 'A premise needs a street address.'],
          ['city', 'A premise needs a village or town.'],
          ['region', 'A premise needs an island.'],
        ] as const) {
          if (values[field] === '') {
            context.addIssue({ code: 'custom', path: [field], message });
          }
        }
      } else if (values.serviceLocationId === '') {
        context.addIssue({
          code: 'custom',
          path: ['serviceLocationId'],
          message: 'Pick the premise this account is for.',
        });
      }

      const collected = parseDeposit(values.depositCollected);
      const reject = (message: string) =>
        context.addIssue({ code: 'custom', path: ['depositCollected'], message });

      if (Number.isNaN(collected)) {
        reject('A deposit is an amount of money.');
        return;
      }

      if (collected < 0) {
        reject('A deposit collected cannot be negative.');
        return;
      }

      if (!isWholeCents(collected)) {
        reject('A deposit must be a whole number of cents.');
        return;
      }

      if (collected > 0 && !mayCollectDeposit) {
        // The same refusal the host makes, made before the request rather than after it.
        reject('You do not hold the permission to collect a deposit. Register without one and ask somebody who does.');
        return;
      }

      if (assessedAmount !== undefined && toCents(collected) > toCents(assessedAmount)) {
        reject(
          `The schedule asks ${assessedAmount.toFixed(2)} for a ${values.class.toLowerCase()} ${values.serviceType.toLowerCase()} account. Collect that or less.`,
        );
      }
    });
}

/**
 * The single request the whole form becomes.
 *
 * One call, not four: the customer, the premise and the account are written in one host-side
 * transaction, which is the whole reason an abandoned wizard leaves nothing behind. Empty optional
 * boxes become `null` rather than `""` — an empty string is a value, and the host would store it.
 */
export function buildIntake(values: IntakeValues): CustomerIntakeInput {
  return {
    name: values.name.trim(),
    class: values.class,
    contactName: blankToNull(values.contactName),
    email: blankToNull(values.email),
    phone: blankToNull(values.phone),
    premise:
      values.premiseMode === 'new'
        ? {
            newPremise: {
              address: {
                line1: values.line1.trim(),
                line2: blankToNull(values.line2),
                city: values.city.trim(),
                region: values.region.trim(),
                postalCode: blankToNull(values.postalCode),
                // MP — the demo world is the Marianas, and the host stores the country on the address.
                country: 'MP',
              },
              description: blankToNull(values.description),
            },
          }
        : { serviceLocationId: values.serviceLocationId },
    serviceType: values.serviceType,
    depositCollected: parseDeposit(values.depositCollected),
    startService: values.startService,
    reason: blankToNull(values.reason),
  };
}

function blankToNull(value: string): string | null {
  const trimmed = value.trim();

  return trimmed === '' ? null : trimmed;
}

/**
 * The premises an account may still be opened at: active, and not already served.
 *
 * A convenience, not a guarantee — the host refuses an occupied premise with a 409 naming the
 * account holding it, and that refusal is the real rule. This only keeps a clerk from picking one
 * that is plainly taken.
 */
export function availablePremises(
  locations: readonly ServiceLocation[] | undefined,
  accounts: readonly ServiceAccount[] | undefined,
  serviceType: ServiceType,
): ServiceLocation[] {
  // Narrowed to the SAME SUPPLY since WP-2.17. A premise already on electricity is a perfectly good
  // premise to open a water account at — what the host refuses is a second account for the supply
  // being applied for, so that is what this hides.
  const served = new Set(
    (accounts ?? [])
      .filter((account) => account.status !== 'Closed' && account.serviceType === serviceType)
      .map((account) => account.serviceLocationId),
  );

  return (locations ?? []).filter((location) => location.isActive && !served.has(location.id));
}

/** One line of the review step: what will be written, in the words the form used. */
export type ReviewFact = { label: string; value: string; numeric?: boolean };

/**
 * The review step's summary. A pure projection of the form, so what a clerk reads before pressing
 * the button is testable without rendering anything.
 */
export function reviewFacts(
  values: IntakeValues,
  assessment: DepositRule | undefined,
  premiseLabel: string | undefined,
): ReviewFact[] {
  const collected = parseDeposit(values.depositCollected);

  return [
    { label: 'Customer', value: values.name.trim() },
    { label: 'Class', value: values.class },
    { label: 'Contact', value: dash(values.contactName) },
    { label: 'Email', value: dash(values.email) },
    { label: 'Phone', value: dash(values.phone) },
    {
      label: values.premiseMode === 'new' ? 'New premise' : 'Existing premise',
      value: values.premiseMode === 'new' ? newPremiseLine(values) : (premiseLabel ?? '—'),
    },
    {
      label: 'Service',
      value: isMeteredService(values.serviceType)
        ? serviceTypeLabel(values.serviceType)
        : `${serviceTypeLabel(values.serviceType)} (unmetered)`,
    },
    { label: 'Supply', value: values.startService ? 'Energised on registration' : 'Account opened, not energised' },
    {
      label: 'Deposit assessed',
      value: assessment ? assessment.amount.toFixed(2) : '—',
      numeric: true,
    },
    { label: 'Deposit collected', value: collected.toFixed(2), numeric: true },
  ];
}

function newPremiseLine(values: IntakeValues): string {
  return [values.line1, values.line2, values.city, values.region, values.postalCode]
    .map((part) => part.trim())
    .filter((part) => part !== '')
    .join(', ');
}

function dash(value: string): string {
  return value.trim() === '' ? '—' : value.trim();
}
