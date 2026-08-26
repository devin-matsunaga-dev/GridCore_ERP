import { z } from 'zod';
import {
  billDeliveryChannels,
  communicationLanguages,
  contactMethodKinds,
  type BillDeliveryChannel,
  type CommunicationLanguage,
  type ContactMethod,
  type ContactMethodKind,
  type CustomerContact,
  type CustomerProfile,
  type UpdateCustomerProfileInput,
} from '@/api/customers';

/**
 * The contacts tab's logic, with no DOM in sight.
 *
 * The two claims on that screen a rep would dispute — which number is the primary one, and where a
 * bill would actually be posted — are worked out here and tested without rendering anything, the
 * same call `customer-360.ts` and `registration.ts` already made.
 */

/** The order the kinds read in, which is the order the host declares them in. */
export const contactMethodKindOrder: readonly ContactMethodKind[] = contactMethodKinds;

const kindLabels: Record<ContactMethodKind, string> = {
  Phone: 'Phone',
  Mobile: 'Mobile',
  Email: 'Email',
};

/** What a method's kind reads as. */
export function methodKindLabel(kind: ContactMethodKind): string {
  return kindLabels[kind];
}

/** The method to try first for `kind`, or `undefined` when the contact holds none of that kind. */
export function primaryMethod(contact: CustomerContact, kind: ContactMethodKind): ContactMethod | undefined {
  return contact.methods.find((method) => method.kind === kind && method.isPrimary);
}

/**
 * A contact's methods grouped by kind, in the kinds' own order, primary first inside each.
 *
 * Kinds the contact holds nothing of are left out rather than rendered empty: a drawer listing
 * "Mobile — none" three times reads as missing data, when what it means is that nobody has one.
 */
export function methodsByKind(contact: CustomerContact): { kind: ContactMethodKind; methods: ContactMethod[] }[] {
  return contactMethodKindOrder
    .map((kind) => ({
      kind,
      methods: contact.methods
        .filter((method) => method.kind === kind)
        .toSorted((a, b) => Number(b.isPrimary) - Number(a.isPrimary)),
    }))
    .filter((group) => group.methods.length > 0);
}

/**
 * The one line a table row shows: each kind's primary, in the kinds' order.
 *
 * The primaries and not every number — a row is what a rep reads while the caller is talking, and
 * the rest of a contact is one click away in the drawer.
 */
export function primaryMethodSummary(contact: CustomerContact): string {
  return contactMethodKindOrder
    .map((kind) => primaryMethod(contact, kind)?.value)
    .filter((value): value is string => value !== undefined)
    .join(' · ');
}

/**
 * Contacts in the order a rep wants them before touching a column header: the people the account
 * may be discussed with first, then oldest first as the host returned them.
 *
 * The same call `sortAccounts` makes for open accounts above closed ones. Choosing a column takes
 * over from there.
 */
export function sortContacts(contacts: readonly CustomerContact[]): CustomerContact[] {
  return contacts.toSorted((a, b) => {
    if (a.isAuthorisedToDiscuss !== b.isAuthorisedToDiscuss) {
      return Number(b.isAuthorisedToDiscuss) - Number(a.isAuthorisedToDiscuss);
    }

    return a.recordedAt.localeCompare(b.recordedAt);
  });
}

/** What a contact's authorisation reads as in a pill. */
export function authorisationLabel(contact: CustomerContact): string {
  return contact.isAuthorisedToDiscuss ? 'May discuss' : 'Not authorised';
}

// ---------------------------------------------------------------------------------------------
// The contact form
// ---------------------------------------------------------------------------------------------

/** Longest name the host stores — `CustomerContact.NameLength`. */
export const contactNameLength = 256;

/** Longest relationship the host stores — `CustomerContact.RelationshipLength`. */
export const relationshipLength = 64;

export const contactSchema = z.object({
  name: z.string().trim().min(1, 'A contact needs a name.').max(contactNameLength),
  relationship: z.string().trim().max(relationshipLength).optional(),
  isAuthorisedToDiscuss: z.boolean(),
});

export type ContactValues = z.infer<typeof contactSchema>;

/** A blank contact form. */
export const emptyContact: ContactValues = {
  name: '',
  relationship: '',
  isAuthorisedToDiscuss: false,
};

/** The form as it opens on an existing contact. */
export function contactValuesFrom(contact: CustomerContact): ContactValues {
  return {
    name: contact.name,
    relationship: contact.relationship ?? '',
    isAuthorisedToDiscuss: contact.isAuthorisedToDiscuss,
  };
}

// ---------------------------------------------------------------------------------------------
// The contact-method form
// ---------------------------------------------------------------------------------------------

/** Longest value the host stores for a phone or mobile — `Customer.PhoneLength`. */
export const phoneValueLength = 32;

/** Longest value the host stores for an email — `Customer.EmailLength`. */
export const emailValueLength = 320;

/** The width a method of `kind` may be, mirroring `ContactMethod.MaxLengthFor`. */
export function methodValueLength(kind: ContactMethodKind): number {
  return kind === 'Email' ? emailValueLength : phoneValueLength;
}

/**
 * A method's rules, per kind.
 *
 * Mirrors the host deliberately — this is a form that saves, and WP-2.8's lesson is that a form
 * which discovers on submit what it could have said while the caller was still on the telephone has
 * wasted the call. (WP-2.9's search duplicates nothing for the opposite reason: a search only asks.)
 */
export const contactMethodSchema = z
  .object({
    kind: z.enum(contactMethodKinds),
    value: z.string().trim().min(1, 'A contact method needs a value.'),
  })
  .superRefine((values, context) => {
    if (values.value.length > methodValueLength(values.kind)) {
      context.addIssue({
        code: 'custom',
        path: ['value'],
        message: `A ${methodKindLabel(values.kind).toLowerCase()} may be at most ${methodValueLength(values.kind)} characters.`,
      });
    }

    if (values.kind === 'Email' && !z.email().safeParse(values.value).success) {
      context.addIssue({ code: 'custom', path: ['value'], message: 'That is not an email address.' });
    }
  });

export type ContactMethodValues = z.infer<typeof contactMethodSchema>;

/** A blank method form. Phone first, because it is the kind a counter takes most often. */
export const emptyContactMethod: ContactMethodValues = { kind: 'Phone', value: '' };

/**
 * Whether `value` would duplicate a method the contact already holds of that kind.
 *
 * The host refuses it and this says so first. Compared case-insensitively and literally — the same
 * comparison `CustomerContact` makes, punctuation and all, so the two cannot disagree about what
 * counts as the same number.
 */
export function duplicatesExistingMethod(
  contact: CustomerContact,
  kind: ContactMethodKind,
  value: string,
  exceptMethodId?: string,
): boolean {
  const trimmed = value.trim().toLowerCase();

  return contact.methods.some(
    (method) =>
      method.kind === kind && method.id !== exceptMethodId && method.value.toLowerCase() === trimmed,
  );
}

// ---------------------------------------------------------------------------------------------
// The profile form
// ---------------------------------------------------------------------------------------------

export type ProfileValues = {
  billDeliveryChannel: BillDeliveryChannel;
  outageNotices: boolean;
  dunningNotices: boolean;
  preferredLanguage: CommunicationLanguage;
  /** On while post follows the service address. Off is what makes the address fields matter. */
  useServiceAddress: boolean;
  mailingAddress: {
    line1: string;
    line2: string;
    city: string;
    region: string;
    postalCode: string;
    country: string;
  };
};

/**
 * The profile form's rules.
 *
 * Built per validation from what the customer actually has, because one rule is not knowable up
 * front: **email delivery needs an email on file**, and whether there is one is a fact about the
 * customer record rather than about this form. The host refuses it either way; saying so here means
 * the rep hears it while they are still looking at the customer. Same shape `intakeSchema` uses.
 */
export function profileSchema({ hasEmail }: { hasEmail: boolean }) {
  return z
    .object({
      billDeliveryChannel: z.enum(billDeliveryChannels),
      outageNotices: z.boolean(),
      dunningNotices: z.boolean(),
      preferredLanguage: z.enum(communicationLanguages),
      useServiceAddress: z.boolean(),
      mailingAddress: z.object({
        line1: z.string().trim().max(200),
        line2: z.string().trim().max(200),
        city: z.string().trim().max(128),
        region: z.string().trim().max(128),
        postalCode: z.string().trim().max(16),
        country: z.string().trim().max(64),
      }),
    })
    .superRefine((values, context) => {
      if (values.billDeliveryChannel !== 'Post' && !hasEmail) {
        context.addIssue({
          code: 'custom',
          path: ['billDeliveryChannel'],
          message: 'This customer has no email address, so bills cannot be delivered by email.',
        });
      }

      if (values.useServiceAddress) return;

      // A separate mailing address that is only half typed is worse than none: post would go to a
      // town with no street. The four parts the host requires are required here.
      for (const field of ['line1', 'city', 'region', 'country'] as const) {
        if (values.mailingAddress[field].length === 0) {
          context.addIssue({
            code: 'custom',
            path: ['mailingAddress', field],
            message: 'Required for a separate mailing address.',
          });
        }
      }
    });
}

/** A blank address, which is what the fields hold while post follows the service address. */
const emptyMailingAddress: ProfileValues['mailingAddress'] = {
  line1: '',
  line2: '',
  city: '',
  region: '',
  postalCode: '',
  // MP — the demo world is the Marianas, the same default the intake wizard's premise step carries.
  country: 'MP',
};

/**
 * The form as it opens on a profile.
 *
 * The toggle reads off `source`, not off whether the address is present: the host answers with the
 * **resolved** address either way, so a form that asked "is there an address" would open with the
 * override switched on for every customer who has a service account.
 */
export function profileValuesFrom(profile: CustomerProfile): ProfileValues {
  const override = profile.source === 'Override' ? profile.mailingAddress : null;

  return {
    billDeliveryChannel: profile.billDeliveryChannel,
    outageNotices: profile.outageNotices,
    dunningNotices: profile.dunningNotices,
    preferredLanguage: profile.preferredLanguage,
    useServiceAddress: profile.source !== 'Override',
    mailingAddress: override
      ? {
          line1: override.line1,
          line2: override.line2 ?? '',
          city: override.city,
          region: override.region,
          postalCode: override.postalCode ?? '',
          country: override.country,
        }
      : emptyMailingAddress,
  };
}

/**
 * What the form sends.
 *
 * `mailingAddress: null` is the cleared override, and the host reads it as "post follows the service
 * address" rather than as "no address" — which is why this is a whole-profile PUT and not a patch.
 */
export function buildProfileInput(values: ProfileValues): UpdateCustomerProfileInput {
  return {
    billDeliveryChannel: values.billDeliveryChannel,
    outageNotices: values.outageNotices,
    dunningNotices: values.dunningNotices,
    preferredLanguage: values.preferredLanguage,
    mailingAddress: values.useServiceAddress
      ? null
      : {
          line1: values.mailingAddress.line1.trim(),
          line2: values.mailingAddress.line2.trim() || null,
          city: values.mailingAddress.city.trim(),
          region: values.mailingAddress.region.trim(),
          postalCode: values.mailingAddress.postalCode.trim() || null,
          country: values.mailingAddress.country.trim(),
        },
  };
}

/** What the mailing-address card says about where the address came from. */
export function mailingAddressSourceLabel(profile: CustomerProfile): string {
  switch (profile.source) {
    case 'Override':
      return 'Separate mailing address';
    case 'ServiceAddress':
      return 'Same as the service address';
    default:
      // Not "no address": the customer holds no service account to fall back to, which is a fact
      // about the account rather than about the profile, and a rep needs to know which it is.
      return 'No service address to fall back to';
  }
}
