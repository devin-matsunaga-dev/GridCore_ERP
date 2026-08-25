import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import type { DepositRule } from '@/api/customers';
import { testUser } from '@/test/render';
import { stubFetch, type FetchStub, type StubbedResponse } from '@/test/api-stub';
import { customer, serviceAccount, serviceLocation } from '@/test/registry-fixtures';
import { renderWithProviders } from '@/test/render';
import { CustomerRegistrationPage } from './customer-registration-page';

/**
 * The intake wizard on screen. What these prove that the pure tests cannot: the steps really are
 * walkable forward and back without losing a keystroke, the deposit box is gated by what the
 * caller holds, and the whole form leaves as **one** POST — the claim the single transaction rests
 * on, and one that four separate calls would quietly break without any of them failing.
 */

const schedule: DepositRule[] = [
  {
    customerClass: 'Residential',
    amount: 75,
    description: 'Two months of a typical household bill.',
    ruleId: '0192f000-0000-7000-8000-0000000000d1',
  },
  {
    customerClass: 'Commercial',
    amount: 450,
    description: 'Two months of a small-premises bill.',
    ruleId: '0192f000-0000-7000-8000-0000000000d2',
  },
];

let stub: FetchStub;

afterEach(() => stub?.restore());

function world(url: URL): StubbedResponse | undefined {
  if (url.pathname === '/api/deposit-rules') return { body: schedule };
  if (url.pathname === '/api/service-locations') return { body: [serviceLocation()] };
  if (url.pathname === '/api/service-accounts') return { body: [] };
  if (url.pathname === '/api/customer-registrations') {
    return {
      status: 201,
      body: {
        customer: customer({ name: 'Reyes Family Residence', accountNumber: 'C-000009' }),
        location: serviceLocation(),
        locationWasRegistered: true,
        account: serviceAccount({ accountNumber: 'A-000009', status: 'Active' }),
        deposit: {
          customerClass: 'Residential',
          assessedAmount: 75,
          collectedAmount: 75,
          ruleId: schedule[0].ruleId,
        },
      },
    };
  }

  return undefined;
}

/** `permissions` decides whether the deposit box is usable — the wizard's own RBAC surface. */
function renderPage(permissions: string[] = ['customers.read', 'customers.write', 'customers.deposit']) {
  stub = stubFetch(world);

  return renderWithProviders(<CustomerRegistrationPage />, {
    route: '/customers/new',
    currentUser: { ...testUser, permissions },
  });
}

/** Fills in step 1 and moves on. */
async function completeIdentity(name = 'Reyes Family Residence') {
  await userEvent.clear(screen.getByLabelText('Customer name'));
  await userEvent.type(screen.getByLabelText('Customer name'), name);
  await userEvent.click(screen.getByRole('button', { name: 'Next' }));
}

/** Fills in step 2's new-premise address and moves on. */
async function completePremise() {
  await userEvent.type(await screen.findByLabelText('Street address'), '77 As Nieves Road');
  await userEvent.type(screen.getByLabelText('Village'), 'Songsong');
  await userEvent.type(screen.getByLabelText('Island'), 'Rota');
  await userEvent.click(screen.getByRole('button', { name: 'Next' }));
}

describe('CustomerRegistrationPage', () => {
  it('opens on the first step and shows the whole flow ahead of it', async () => {
    renderPage();

    expect(await screen.findByRole('heading', { name: 'Identity and contacts' })).toBeInTheDocument();

    // A wizard that hides where it is going is a form with a surprise in it.
    for (const step of ['Service location', 'Service account', 'Deposit', 'Review and register']) {
      expect(screen.getByText(step)).toBeInTheDocument();
    }

    expect(screen.getByText('Step 1 of 5')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Back' })).toBeDisabled();
  });

  it('refuses to move on until the step it is on is valid', async () => {
    renderPage();
    await screen.findByLabelText('Customer name');

    await userEvent.click(screen.getByRole('button', { name: 'Next' }));

    expect(await screen.findByText('A customer needs a name.')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Identity and contacts' })).toBeInTheDocument();
  });

  it('keeps every keystroke when the walk goes back and forward again', async () => {
    renderPage();
    await screen.findByLabelText('Customer name');

    await completeIdentity('Taisacan Household');
    await completePremise();

    expect(await screen.findByRole('heading', { name: 'Service account' })).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Back' }));
    await userEvent.click(screen.getByRole('button', { name: 'Back' }));

    // The point of one form behind five steps: nothing is remounted, so nothing is retyped.
    expect(await screen.findByLabelText('Customer name')).toHaveValue('Taisacan Household');

    await userEvent.click(screen.getByRole('button', { name: 'Next' }));

    expect(await screen.findByLabelText('Street address')).toHaveValue('77 As Nieves Road');
  });

  it('assesses the deposit from the schedule, and follows the class', async () => {
    renderPage();
    await screen.findByLabelText('Customer name');

    await userEvent.selectOptions(screen.getByLabelText('Class'), 'Commercial');
    await completeIdentity();
    await completePremise();
    await userEvent.click(await screen.findByRole('button', { name: 'Next' }));

    expect(await screen.findByText('Assessed for a commercial connection')).toBeInTheDocument();
    expect(screen.getByText('$450.00')).toBeInTheDocument();
    expect(screen.getByText('Two months of a small-premises bill.')).toBeInTheDocument();
  });

  it('refuses more than the schedule asks for, on the step that asked for it', async () => {
    renderPage();
    await screen.findByLabelText('Customer name');

    await completeIdentity();
    await completePremise();
    await userEvent.click(await screen.findByRole('button', { name: 'Next' }));

    await userEvent.type(await screen.findByLabelText('Collected now'), '500');
    await userEvent.click(screen.getByRole('button', { name: 'Next' }));

    expect(
      await screen.findByText('The schedule asks 75.00 for this class. Collect that or less.'),
    ).toBeInTheDocument();

    // Still on the deposit step: the wizard did not carry a doomed figure to the review.
    expect(screen.getByRole('heading', { name: 'Deposit' })).toBeInTheDocument();
  });

  it('disables the deposit box for a caller without the permission, and says why', async () => {
    // The failure path the work package names, as the UI's half of it: the host answers 403, and
    // this is how somebody finds out before spending five steps earning one.
    renderPage(['customers.read', 'customers.write']);
    await screen.findByLabelText('Customer name');

    await completeIdentity();
    await completePremise();
    await userEvent.click(await screen.findByRole('button', { name: 'Next' }));

    expect(await screen.findByLabelText('Collected now')).toBeDisabled();
    expect(screen.getByText(/no deposit can be taken on this intake/)).toBeInTheDocument();
    expect(screen.getByText('customers.deposit')).toBeInTheDocument();

    // The assessed figure is still shown: a clerk who may not take a deposit still has to be able
    // to tell the caller what one costs.
    expect(screen.getByText('$75.00')).toBeInTheDocument();
  });

  it('carries the supply decision through to the review', async () => {
    // The one control that does not go through `register` — it sets a boolean from a select — so
    // the path from the choice to the request is worth walking rather than assuming.
    renderPage();
    await screen.findByLabelText('Customer name');

    await completeIdentity();
    await completePremise();

    await userEvent.selectOptions(await screen.findByLabelText('Supply'), 'Open the account only');
    await userEvent.click(screen.getByRole('button', { name: 'Next' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Next' }));

    expect(await screen.findByText('Account opened, not energised')).toBeInTheDocument();
  });

  it('reviews what will be written before anything is sent', async () => {
    renderPage();
    await screen.findByLabelText('Customer name');

    await completeIdentity();
    await completePremise();
    await userEvent.click(await screen.findByRole('button', { name: 'Next' }));
    await userEvent.type(await screen.findByLabelText('Collected now'), '75');
    await userEvent.click(screen.getByRole('button', { name: 'Next' }));

    const review = await screen.findByRole('heading', { name: 'Review and register' });
    expect(review).toBeInTheDocument();

    expect(screen.getByText('77 As Nieves Road, Songsong, Rota')).toBeInTheDocument();
    expect(screen.getByText('Energised on registration')).toBeInTheDocument();
    expect(screen.getByText(/Nothing above has been saved yet/)).toBeInTheDocument();

    // Nothing has been written, and the wizard has not pretended otherwise.
    expect(stub.calls.filter((url) => url.pathname === '/api/customer-registrations')).toHaveLength(0);
  });

  it('sends the whole intake as one request', async () => {
    renderPage();
    await screen.findByLabelText('Customer name');

    await completeIdentity();
    await completePremise();
    await userEvent.click(await screen.findByRole('button', { name: 'Next' }));
    await userEvent.type(await screen.findByLabelText('Collected now'), '75');
    await userEvent.click(screen.getByRole('button', { name: 'Next' }));

    await userEvent.click(await screen.findByRole('button', { name: 'Register customer' }));

    await waitFor(() => {
      expect(stub.calls.filter((url) => url.pathname === '/api/customer-registrations')).toHaveLength(1);
    });

    // And the three registry endpoints the demonstration walk calls one at a time are never touched:
    // an abandoned wizard leaving nothing behind is only true while this stays a single commit.
    expect(stub.calls.filter((url) => url.pathname === '/api/customers')).toHaveLength(0);
    expect(
      stub.calls.filter((url) => url.pathname === '/api/service-accounts' && url.search === ''),
    ).not.toHaveLength(2);
  });

  it('offers an existing premise instead of an address, when there is one free', async () => {
    renderPage();
    await screen.findByLabelText('Customer name');

    await completeIdentity();

    await userEvent.selectOptions(await screen.findByLabelText('Premise'), 'Use an existing premise');

    const picker = await screen.findByLabelText('Existing premise');

    expect(within(picker).getAllByRole('option').map((option) => option.textContent)).toEqual([
      'Choose a premise…',
      'L-000001 — 12 Songsong Village Road, Songsong, Rota, MP 96951',
    ]);

    // Nothing picked yet, so the step will not let the walk past it.
    await userEvent.click(screen.getByRole('button', { name: 'Next' }));

    expect(await screen.findByText('Pick the premise this account is for.')).toBeInTheDocument();
  });
});
