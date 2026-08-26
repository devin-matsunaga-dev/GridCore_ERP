import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router';
import { afterEach, describe, expect, it } from 'vitest';
import { CustomerDetailPage } from './customer-detail-page';
import { stubFetch, type FetchStub, type StubbedResponse } from '@/test/api-stub';
import {
  contactMethod,
  customer,
  customerContact,
  customerProfile,
  serviceLocation,
} from '@/test/registry-fixtures';
import { renderWithProviders } from '@/test/render';

/**
 * The contacts tab (WP-2.11): who a rep may speak to, and where post goes.
 *
 * Its own file rather than more of `customer-detail-page.test.tsx`, which is already the 360's
 * whole surface — the same split `customer-registration-page.test.tsx` made for the wizard.
 *
 * Everything here drives the real API client through a stubbed `fetch`, so the URL each action
 * produced is part of what is asserted: a promotion that stopped reaching the host would otherwise
 * still look right on screen, because the table re-renders from the refetch either way.
 */

const record = customer();
const premise = serviceLocation();

const spouse = customerContact();

const landlord = customerContact({
  id: '0192f000-0000-7000-8000-000000000502',
  name: 'Antonio Reyes',
  relationship: 'Landlord',
  isAuthorisedToDiscuss: false,
  recordedAt: '2026-02-12T00:30:00+00:00',
  methods: [
    contactMethod({ id: 'method-office', kind: 'Phone', value: '+1 670 555 0101', isPrimary: true }),
    contactMethod({ id: 'method-spare', kind: 'Phone', value: '+1 670 555 0102', isPrimary: false }),
  ],
});

let stub: FetchStub;

afterEach(() => stub?.restore());

const contactsPath = `/api/customers/${record.id}/contacts`;
const profilePath = `/api/customers/${record.id}/profile`;

/** The 360 with its contacts tab answered, and every other panel empty. */
function world(overrides: (url: URL) => StubbedResponse | undefined = () => undefined) {
  return (url: URL): StubbedResponse | undefined => {
    const override = overrides(url);
    if (override) return override;

    if (url.pathname === `/api/customers/${record.id}`) return { body: record };
    if (url.pathname === contactsPath) return { body: [spouse, landlord] };
    if (url.pathname === profilePath) return { body: customerProfile() };
    if (url.pathname === '/api/service-accounts') return { body: [] };
    if (url.pathname === '/api/bills') return { body: [] };
    if (url.pathname === '/api/payments') return { body: [] };

    return undefined;
  };
}

function renderTab(respond: (url: URL) => StubbedResponse | undefined = world()) {
  stub = stubFetch(respond);

  return renderWithProviders(
    <Routes>
      <Route path="/customers/:customerId" element={<CustomerDetailPage />} />
      <Route path="/customers/:customerId/:tab" element={<CustomerDetailPage />} />
    </Routes>,
    { route: `/customers/${record.id}/contacts` },
  );
}

/** Renders the tab and hands back the contacts table, which is where most of these start. */
async function contactsTable(respond: (url: URL) => StubbedResponse | undefined = world()) {
  renderTab(respond);

  return within(await screen.findByRole('table', { name: 'Contacts' }));
}

describe('the contacts tab', () => {
  it('is a route, so the tab a rep pastes is the tab that opens', async () => {
    renderTab();

    expect(await screen.findByRole('table', { name: 'Contacts' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Customer record' })).not.toBeInTheDocument();
  });

  it('lists the contacts as a table, not as a card each', async () => {
    const table = await contactsTable();

    expect(table.getByText('Rosa Taimanao')).toBeInTheDocument();
    expect(table.getByText('Antonio Reyes')).toBeInTheDocument();
    expect(table.getByText('Landlord')).toBeInTheDocument();
  });

  it('puts the contacts the account may be discussed with first', async () => {
    const table = await contactsTable();
    const names = table.getAllByText(/Taimanao|Reyes/).map((node) => node.textContent);

    // The order a rep wants before touching a column header. Choosing a column takes over.
    expect(names).toEqual(['Rosa Taimanao', 'Antonio Reyes']);
  });

  it('shows one primary per kind on the row, not every number', async () => {
    const table = await contactsTable();

    expect(table.getByText('+1 670 555 0188')).toBeInTheDocument();
    expect(table.getByText('+1 670 555 0101')).toBeInTheDocument();

    // The landlord's second number is in the drawer, not in the row.
    expect(table.queryByText('+1 670 555 0102')).not.toBeInTheDocument();
  });

  it('says who the account may be discussed with in words, not as a flag', async () => {
    const table = await contactsTable();

    expect(table.getByText('May discuss')).toBeInTheDocument();
    expect(table.getByText('Not authorised')).toBeInTheDocument();
  });

  it('opens a contact in a drawer with every method grouped by kind', async () => {
    const table = await contactsTable();
    await userEvent.click(table.getByRole('button', { name: /Antonio Reyes/ }));

    const drawer = within(await screen.findByRole('dialog'));

    expect(drawer.getByText('+1 670 555 0101')).toBeInTheDocument();
    expect(drawer.getByText('+1 670 555 0102')).toBeInTheDocument();
    expect(drawer.getByText('Primary')).toBeInTheDocument();

    // The table is still behind it.
    expect(screen.getByRole('table', { name: 'Contacts' })).toBeInTheDocument();
  });

  /**
   * Promotion is a POST sub-resource rather than a PUT of a boolean, because it changes a row the
   * caller did not name: whichever method held the primary place is demoted in the same act.
   */
  it('promotes a method through its own endpoint', async () => {
    const table = await contactsTable();
    await userEvent.click(table.getByRole('button', { name: /Antonio Reyes/ }));

    const drawer = within(await screen.findByRole('dialog'));
    await userEvent.click(drawer.getByRole('button', { name: /Make \+1 670 555 0102 the primary phone/ }));

    expect(
      stub.calls.some(
        (url) => url.pathname === `/api/customer-contacts/${landlord.id}/methods/method-spare/primary`,
      ),
    ).toBe(true);
  });

  it('refuses a duplicate before asking the host', async () => {
    const table = await contactsTable();
    await userEvent.click(table.getByRole('button', { name: /Antonio Reyes/ }));

    const drawer = within(await screen.findByRole('dialog'));

    await userEvent.type(drawer.getByLabelText('Number or address'), '+1 670 555 0101');
    await userEvent.click(drawer.getByRole('button', { name: 'Add' }));

    expect(await screen.findByText('This contact already has that phone.')).toBeInTheDocument();

    // Nothing left the browser: the host would refuse it, and saying so first saves the round trip.
    expect(stub.calls.every((url) => !url.pathname.endsWith('/methods'))).toBe(true);
  });

  it('refuses an email that is not one, per kind', async () => {
    const table = await contactsTable();
    await userEvent.click(table.getByRole('button', { name: /Antonio Reyes/ }));

    const drawer = within(await screen.findByRole('dialog'));

    await userEvent.selectOptions(drawer.getByLabelText('Kind'), 'Email');
    await userEvent.type(drawer.getByLabelText('Number or address'), 'not-an-address');
    await userEvent.click(drawer.getByRole('button', { name: 'Add' }));

    expect(await screen.findByText('That is not an email address.')).toBeInTheDocument();
  });

  it('adds a contact against this customer', async () => {
    await contactsTable();

    await userEvent.click(screen.getByRole('button', { name: 'Add contact' }));
    await userEvent.type(screen.getByLabelText('Name'), 'Jesus Camacho');
    await userEvent.click(screen.getByRole('button', { name: 'Add contact' }));

    expect(stub.calls.some((url) => url.pathname === contactsPath)).toBe(true);
  });

  it('will not add a contact with no name', async () => {
    await contactsTable();

    await userEvent.click(screen.getByRole('button', { name: 'Add contact' }));

    const before = stub.calls.length;
    await userEvent.click(screen.getByRole('button', { name: 'Add contact' }));

    expect(await screen.findByText('A contact needs a name.')).toBeInTheDocument();
    expect(stub.calls.length).toBe(before);
  });
});

describe('the mailing address and preferences', () => {
  it('says the address on screen is the service address, not a separate one', async () => {
    renderTab();

    expect(await screen.findByText(premise.formattedAddress)).toBeInTheDocument();
    expect(screen.getByText('Same as the service address')).toBeInTheDocument();
  });

  it('says nobody has expressed a preference yet rather than showing a date', async () => {
    renderTab();

    // Null `updatedAt` is the difference between "nobody has said" and "somebody chose exactly this".
    expect(await screen.findByText('Still on the defaults')).toBeInTheDocument();
  });

  it('shows a separate mailing address as the override it is', async () => {
    renderTab(
      world((url) =>
        url.pathname === profilePath
          ? {
              body: customerProfile({
                source: 'Override',
                mailingAddress: { ...premise.address, line1: 'PO Box 501' },
                formattedMailingAddress: 'PO Box 501, Songsong, Rota, MP 96951',
                updatedAt: '2026-06-01T00:30:00+00:00',
              }),
            }
          : undefined,
      ),
    );

    expect(await screen.findByText('PO Box 501, Songsong, Rota, MP 96951')).toBeInTheDocument();
    expect(screen.getByText('Separate mailing address')).toBeInTheDocument();
  });

  it('opens the form with the override off while post follows the service address', async () => {
    renderTab();
    await screen.findByText('Same as the service address');

    await userEvent.click(screen.getByRole('button', { name: 'Edit' }));

    const toggle = await screen.findByLabelText('Post goes to the service address');

    // Read off `source`, not off whether an address came back: the host answers with the resolved
    // address either way, so "is there an address" would switch the override on for everybody.
    expect(toggle).toBeChecked();
    expect(screen.queryByLabelText('Street address')).not.toBeInTheDocument();
  });

  it('asks for the address parts once the override is switched on', async () => {
    renderTab();
    await screen.findByText('Same as the service address');

    await userEvent.click(screen.getByRole('button', { name: 'Edit' }));
    await userEvent.click(await screen.findByLabelText('Post goes to the service address'));
    await userEvent.click(screen.getByRole('button', { name: 'Save preferences' }));

    expect(await screen.findAllByText('Required for a separate mailing address.')).not.toHaveLength(0);
    expect(stub.calls.every((url) => url.pathname !== profilePath || url.search === '')).toBe(true);
  });

  /**
   * Failure path, and the reason the rule is mirrored in the browser at all: the host refuses email
   * delivery for a customer with no email on file, and a rep who only heard that after pressing
   * save would have let the caller go.
   */
  it('refuses email delivery when the customer has no email on file', async () => {
    renderTab(
      world((url) =>
        url.pathname === `/api/customers/${record.id}` ? { body: customer({ email: null }) } : undefined,
      ),
    );

    await screen.findByText('Same as the service address');
    await userEvent.click(screen.getByRole('button', { name: 'Edit' }));

    await userEvent.selectOptions(await screen.findByLabelText('Bill delivery'), 'Email');
    await userEvent.click(screen.getByRole('button', { name: 'Save preferences' }));

    expect(
      await screen.findByText('This customer has no email address, so bills cannot be delivered by email.'),
    ).toBeInTheDocument();
  });

  it('reports a profile that failed to load rather than showing the defaults', async () => {
    renderTab(
      world((url) =>
        url.pathname === profilePath
          ? { status: 403, body: { title: 'Not permitted', status: 403, detail: 'You may not read that.' } }
          : undefined,
      ),
    );

    // A permission refusal is not a customer on the defaults, and rendering one as the other would
    // put a claim about this customer on screen that nobody made.
    expect(await screen.findByText('You may not read that.')).toBeInTheDocument();
    expect(screen.queryByText('Still on the defaults')).not.toBeInTheDocument();
  });
});
