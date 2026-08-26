import { describe, expect, it } from 'vitest';
import type { Address, CustomerContact, CustomerProfile } from '@/api/customers';
import {
  authorisationLabel,
  buildProfileInput,
  contactMethodSchema,
  contactSchema,
  contactValuesFrom,
  duplicatesExistingMethod,
  mailingAddressSourceLabel,
  methodValueLength,
  methodsByKind,
  primaryMethod,
  primaryMethodSummary,
  profileSchema,
  profileValuesFrom,
  sortContacts,
  type ProfileValues,
} from './contacts';

function aMethod(overrides: Partial<CustomerContact['methods'][number]> = {}): CustomerContact['methods'][number] {
  return {
    id: overrides.id ?? 'method-1',
    kind: overrides.kind ?? 'Phone',
    value: overrides.value ?? '+1-670-532-0114',
    isPrimary: overrides.isPrimary ?? false,
    recordedAt: overrides.recordedAt ?? '2026-08-26T09:00:00Z',
  };
}

function aContact(overrides: Partial<CustomerContact> = {}): CustomerContact {
  return {
    id: 'contact-1',
    customerId: 'customer-1',
    name: 'Rosa Sablan',
    relationship: 'Spouse',
    isAuthorisedToDiscuss: false,
    methods: [],
    recordedAt: '2026-08-26T09:00:00Z',
    ...overrides,
  };
}

const anAddress: Address = {
  line1: '12 Sinapalo Drive',
  line2: null,
  city: 'Songsong',
  region: 'Rota',
  country: 'MP',
  postalCode: null,
};

const aPoBox: Address = { ...anAddress, line1: 'PO Box 501', postalCode: '96951' };

function aProfile(overrides: Partial<CustomerProfile> = {}): CustomerProfile {
  return {
    customerId: 'customer-1',
    mailingAddress: anAddress,
    formattedMailingAddress: '12 Sinapalo Drive, Songsong, Rota',
    source: 'ServiceAddress',
    serviceAddress: anAddress,
    serviceLocationId: 'location-1',
    billDeliveryChannel: 'Post',
    outageNotices: true,
    dunningNotices: true,
    preferredLanguage: 'English',
    updatedAt: null,
    ...overrides,
  };
}

describe('contact methods', () => {
  it('finds the primary of a kind', () => {
    const contact = aContact({
      methods: [
        aMethod({ id: 'a', kind: 'Phone', value: '+1-670-532-0114', isPrimary: false }),
        aMethod({ id: 'b', kind: 'Phone', value: '+1-670-532-9987', isPrimary: true }),
      ],
    });

    expect(primaryMethod(contact, 'Phone')?.id).toBe('b');
  });

  it('has no primary for a kind the contact holds nothing of', () => {
    // The rule is one primary per kind the contact HAS — an absent kind is not a missing primary.
    expect(primaryMethod(aContact(), 'Email')).toBeUndefined();
  });

  it('groups by kind in the kinds order with the primary first', () => {
    const contact = aContact({
      methods: [
        aMethod({ id: 'a', kind: 'Email', value: 'rosa@example.com', isPrimary: true }),
        aMethod({ id: 'b', kind: 'Phone', value: '+1-670-532-0114', isPrimary: false }),
        aMethod({ id: 'c', kind: 'Phone', value: '+1-670-532-9987', isPrimary: true }),
      ],
    });

    const groups = methodsByKind(contact);

    expect(groups.map((group) => group.kind)).toEqual(['Phone', 'Email']);
    expect(groups[0]!.methods.map((method) => method.id)).toEqual(['c', 'b']);
  });

  it('leaves out kinds the contact holds nothing of', () => {
    // "Mobile — none" three times reads as missing data, when it means nobody has one.
    expect(methodsByKind(aContact()).length).toBe(0);
  });

  it('summarises a row as one primary per kind', () => {
    const contact = aContact({
      methods: [
        aMethod({ id: 'a', kind: 'Phone', value: '+1-670-532-0114', isPrimary: true }),
        aMethod({ id: 'b', kind: 'Phone', value: '+1-670-532-9987', isPrimary: false }),
        aMethod({ id: 'c', kind: 'Email', value: 'rosa@example.com', isPrimary: true }),
      ],
    });

    expect(primaryMethodSummary(contact)).toBe('+1-670-532-0114 · rosa@example.com');
  });

  it('summarises a contact with no methods as nothing at all', () => {
    expect(primaryMethodSummary(aContact())).toBe('');
  });

  it('spots a duplicate the host would refuse, case aside', () => {
    const contact = aContact({ methods: [aMethod({ id: 'a', kind: 'Email', value: 'Rosa@example.com' })] });

    expect(duplicatesExistingMethod(contact, 'Email', 'rosa@example.com')).toBe(true);
  });

  it('does not call one value a duplicate across two kinds', () => {
    // A one-person business whose landline diverts to a mobile is ordinary, not a data-entry slip.
    const contact = aContact({ methods: [aMethod({ id: 'a', kind: 'Phone', value: '+1-670-285-1180' })] });

    expect(duplicatesExistingMethod(contact, 'Mobile', '+1-670-285-1180')).toBe(false);
  });

  it('does not call a method a duplicate of itself when it is being corrected', () => {
    const contact = aContact({ methods: [aMethod({ id: 'a', kind: 'Phone', value: '+1-670-285-1180' })] });

    expect(duplicatesExistingMethod(contact, 'Phone', '+1-670-285-1180', 'a')).toBe(false);
  });
});

describe('contact ordering', () => {
  it('puts the contacts the account may be discussed with first', () => {
    const ordered = sortContacts([
      aContact({ id: 'a', isAuthorisedToDiscuss: false, recordedAt: '2026-01-01T00:00:00Z' }),
      aContact({ id: 'b', isAuthorisedToDiscuss: true, recordedAt: '2026-06-01T00:00:00Z' }),
    ]);

    expect(ordered.map((contact) => contact.id)).toEqual(['b', 'a']);
  });

  it('keeps the oldest first within each group', () => {
    const ordered = sortContacts([
      aContact({ id: 'newer', recordedAt: '2026-06-01T00:00:00Z' }),
      aContact({ id: 'older', recordedAt: '2026-01-01T00:00:00Z' }),
    ]);

    expect(ordered.map((contact) => contact.id)).toEqual(['older', 'newer']);
  });

  it('does not mutate what it was given', () => {
    const contacts = [aContact({ id: 'a' }), aContact({ id: 'b', isAuthorisedToDiscuss: true })];

    sortContacts(contacts);

    expect(contacts.map((contact) => contact.id)).toEqual(['a', 'b']);
  });

  it('labels authorisation as what a rep may do, not as a flag', () => {
    expect(authorisationLabel(aContact({ isAuthorisedToDiscuss: true }))).toBe('May discuss');
    expect(authorisationLabel(aContact())).toBe('Not authorised');
  });
});

describe('the contact form', () => {
  it('needs a name', () => {
    expect(contactSchema.safeParse({ name: '  ', isAuthorisedToDiscuss: false }).success).toBe(false);
  });

  it('takes a name alone', () => {
    // A rep who has a name and nothing else has still learnt something worth recording.
    expect(contactSchema.safeParse({ name: 'Rosa Sablan', isAuthorisedToDiscuss: false }).success).toBe(true);
  });

  it('opens on an existing contact with its own values', () => {
    expect(contactValuesFrom(aContact({ isAuthorisedToDiscuss: true }))).toEqual({
      name: 'Rosa Sablan',
      relationship: 'Spouse',
      isAuthorisedToDiscuss: true,
    });
  });

  it('renders a missing relationship as an empty box rather than the word null', () => {
    expect(contactValuesFrom(aContact({ relationship: null })).relationship).toBe('');
  });
});

describe('the contact-method form', () => {
  it('refuses an email that is not one', () => {
    expect(contactMethodSchema.safeParse({ kind: 'Email', value: 'not-an-address' }).success).toBe(false);
  });

  it('does not hold a phone to the email rule', () => {
    // Running the email rule over every kind is how "+1-670-532-0114" gets refused for missing an @.
    expect(contactMethodSchema.safeParse({ kind: 'Phone', value: '+1-670-532-0114' }).success).toBe(true);
  });

  it('needs a value', () => {
    expect(contactMethodSchema.safeParse({ kind: 'Mobile', value: '   ' }).success).toBe(false);
  });

  it('holds a phone to the phone width, not the email one', () => {
    expect(methodValueLength('Phone')).toBeLessThan(methodValueLength('Email'));
    expect(contactMethodSchema.safeParse({ kind: 'Phone', value: '9'.repeat(200) }).success).toBe(false);
  });
});

describe('the profile form', () => {
  const schema = profileSchema({ hasEmail: true });

  const values: ProfileValues = {
    billDeliveryChannel: 'Post',
    outageNotices: true,
    dunningNotices: true,
    preferredLanguage: 'English',
    useServiceAddress: true,
    mailingAddress: { line1: '', line2: '', city: '', region: '', postalCode: '', country: 'MP' },
  };

  it('opens with the override off when post follows the service address', () => {
    // Read off `source`, not off whether an address is present: the host answers with the resolved
    // address either way, so "is there an address" would switch the override on for everybody.
    const opened = profileValuesFrom(aProfile({ source: 'ServiceAddress' }));

    expect(opened.useServiceAddress).toBe(true);
    expect(opened.mailingAddress.line1).toBe('');
  });

  it('opens with the override on and filled when there is one', () => {
    const opened = profileValuesFrom(aProfile({ source: 'Override', mailingAddress: aPoBox }));

    expect(opened.useServiceAddress).toBe(false);
    expect(opened.mailingAddress.line1).toBe('PO Box 501');
    expect(opened.mailingAddress.postalCode).toBe('96951');
  });

  it('sends null for the mailing address when post follows the service address', () => {
    // Null is the CLEARED override, which the host reads as "post follows the service address"
    // rather than as "no address" — the distinction this whole resource carries.
    expect(buildProfileInput(values).mailingAddress).toBeNull();
  });

  it('sends the typed address when the override is on', () => {
    const built = buildProfileInput({
      ...values,
      useServiceAddress: false,
      mailingAddress: { line1: ' PO Box 501 ', line2: '', city: 'Songsong', region: 'Rota', postalCode: '', country: 'MP' },
    });

    expect(built.mailingAddress).toEqual({
      line1: 'PO Box 501',
      line2: null,
      city: 'Songsong',
      region: 'Rota',
      postalCode: null,
      country: 'MP',
    });
  });

  it('needs the address parts once the override is on', () => {
    expect(schema.safeParse({ ...values, useServiceAddress: false }).success).toBe(false);
  });

  it('does not ask for address parts while post follows the service address', () => {
    expect(schema.safeParse(values).success).toBe(true);
  });

  it('refuses email delivery when the customer has no email on file', () => {
    // The host refuses it either way; saying so here means the rep hears it while they are still
    // looking at the customer rather than after pressing save.
    const withoutEmail = profileSchema({ hasEmail: false });

    expect(withoutEmail.safeParse({ ...values, billDeliveryChannel: 'Email' }).success).toBe(false);
    expect(withoutEmail.safeParse({ ...values, billDeliveryChannel: 'Both' }).success).toBe(false);
    expect(withoutEmail.safeParse(values).success).toBe(true);
  });

  it('says where the address on screen came from', () => {
    expect(mailingAddressSourceLabel(aProfile({ source: 'Override' }))).toBe('Separate mailing address');
    expect(mailingAddressSourceLabel(aProfile({ source: 'ServiceAddress' }))).toBe('Same as the service address');
  });

  it('says a customer with no accounts has nothing to fall back to', () => {
    // A fact about the account, not about the profile — and a rep needs to know which it is.
    expect(mailingAddressSourceLabel(aProfile({ source: 'None', mailingAddress: null, serviceAddress: null })))
      .toBe('No service address to fall back to');
  });
});
