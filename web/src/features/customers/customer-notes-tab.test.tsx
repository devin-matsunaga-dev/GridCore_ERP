import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router';
import { afterEach, describe, expect, it } from 'vitest';
import { CustomerDetailPage } from './customer-detail-page';
import { stubFetch, type FetchStub, type StubbedResponse } from '@/test/api-stub';
import { customer, customerNote } from '@/test/registry-fixtures';
import { bill } from '@/test/revenue-cycle-fixtures';
import { renderWithProviders } from '@/test/render';

/**
 * The notes tab (WP-2.13): the append-only log, and the three things a rep does to it — log, correct
 * and pin.
 *
 * Its own file rather than more of `customer-detail-page.test.tsx`, the split the contacts and
 * deposit tabs already made. Everything drives the real API client through a stubbed `fetch`, so the
 * URL each action produced is part of what is asserted — a correction that stopped reaching the host
 * would otherwise still look right on screen, because the table re-renders from the refetch either
 * way.
 */

const record = customer();
const notesPath = `/api/customers/${record.id}/notes`;

let stub: FetchStub;

afterEach(() => stub?.restore());

const owedBill = bill();

/** A standing instruction, pinned, and old enough that a date sort would bury it. */
const standing = customerNote({
  id: '0192f000-0000-7000-8000-00000000c001',
  kind: 'Note',
  isInteraction: false,
  body: 'Dog on the property — sound the horn at the gate.',
  isPinned: true,
  recordedAt: '2026-01-04T00:30:00+00:00',
});

/** A dispute about the bill the page already fetched. */
const dispute = customerNote({
  id: '0192f000-0000-7000-8000-00000000c002',
  kind: 'BillingDispute',
  body: 'Queried the consumption on the August bill.',
  linkKind: 'Bill',
  linkedEntityId: owedBill.id,
  linkedReference: owedBill.billNumber,
  recordedAt: '2026-08-22T00:30:00+00:00',
});

/** The 360 with its notes tab answered, and every other panel empty. */
function world(overrides: (url: URL) => StubbedResponse | undefined = () => undefined) {
  return (url: URL): StubbedResponse | undefined => {
    const override = overrides(url);
    if (override) return override;

    if (url.pathname === `/api/customers/${record.id}`) return { body: record };
    if (url.pathname === notesPath) return { body: [standing, dispute] };
    if (url.pathname === '/api/bills') return { body: [owedBill] };
    if (url.pathname === `/api/customers/${record.id}/contacts`) return { body: [] };
    if (url.pathname === `/api/customers/${record.id}/profile`) return { body: null };
    if (url.pathname === `/api/customers/${record.id}/deposits`) return { body: null };
    if (url.pathname === '/api/service-accounts') return { body: [] };
    if (url.pathname === '/api/payments') return { body: [] };

    return undefined;
  };
}

function renderTab(
  respond: (url: URL) => StubbedResponse | undefined = world(),
  route = `/customers/${record.id}/notes`,
) {
  stub = stubFetch(respond);

  return renderWithProviders(
    <Routes>
      <Route path="/customers/:customerId" element={<CustomerDetailPage />} />
      <Route path="/customers/:customerId/:tab" element={<CustomerDetailPage />} />
    </Routes>,
    { route },
  );
}

async function logTable(respond: (url: URL) => StubbedResponse | undefined = world()) {
  renderTab(respond);

  return within(await screen.findByRole('table', { name: 'Notes and interactions' }));
}

describe('the notes tab', () => {
  it('is a route, so the tab a rep pastes is the tab that opens', async () => {
    renderTab();

    expect(await screen.findByRole('table', { name: 'Notes and interactions' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Customer record' })).not.toBeInTheDocument();
  });

  it('lists the log with the kind, the words and who wrote them', async () => {
    const table = await logTable();

    expect(table.getByText('Dog on the property — sound the horn at the gate.')).toBeInTheDocument();
    expect(table.getByText('Billing dispute')).toBeInTheDocument();
    expect(table.getAllByText('Ana Cruz (demo)').length).toBeGreaterThan(0);
  });

  it('puts the PINNED note first even though it is the oldest', async () => {
    // WORK_PACKAGES.md's rule made visible. The standing instruction is from January and the dispute
    // from August; a date sort would put the standing instruction last, which is exactly the thing
    // pinning exists to prevent.
    const table = await logTable();
    const rows = table.getAllByRole('row').map((row) => row.textContent);

    const pinned = rows.findIndex((text) => text?.includes('Dog on the property'));
    const disputed = rows.findIndex((text) => text?.includes('Queried the consumption'));

    expect(pinned).toBeLessThan(disputed);
  });

  it('names the bill a dispute is about, in its own column', async () => {
    const table = await logTable();

    expect(table.getByText(`Bill ${owedBill.billNumber}`)).toBeInTheDocument();
  });

  it('names a work order WITHOUT a number, because the host does not verify one yet', async () => {
    // WP-2.13's accepted gap where a rep actually meets it. It reads as a plain fact rather than as
    // a missing value; WP-3.1 is what fills the number in.
    const table = await logTable(
      world((url) =>
        url.pathname === notesPath
          ? {
              body: [
                customerNote({
                  body: 'Crew attended after the storm.',
                  linkKind: 'WorkOrder',
                  linkedEntityId: '0192f000-0000-7000-8000-00000000c009',
                  linkedReference: null,
                }),
              ],
            }
          : undefined,
      ),
    );

    expect(table.getByText('Work order')).toBeInTheDocument();
  });

  it('marks a corrected note as corrected and its replacement as a correction', async () => {
    // Derived, never stored: the host keeps no back-pointer on an immutable row, so the browser
    // works out which notes have been superseded from the log it already holds.
    const original = customerNote({
      id: '0192f000-0000-7000-8000-00000000c010',
      body: 'No answer.',
      recordedAt: '2026-08-20T00:30:00+00:00',
    });
    const correction = customerNote({
      id: '0192f000-0000-7000-8000-00000000c011',
      body: 'Answered — test confirmed for Tuesday.',
      correctsNoteId: original.id,
      recordedAt: '2026-08-21T00:30:00+00:00',
    });

    const table = await logTable(
      world((url) => (url.pathname === notesPath ? { body: [original, correction] } : undefined)),
    );

    // Both are on screen. The register keeps what was first written, because the customer may have
    // been told it.
    expect(table.getByText('No answer.')).toBeInTheDocument();
    expect(table.getByText('Answered — test confirmed for Tuesday.')).toBeInTheDocument();

    expect(table.getByText('Corrected')).toBeInTheDocument();
    expect(table.getByText('Correction')).toBeInTheDocument();
  });

  it('has no edit control anywhere, because the host would refuse one', async () => {
    // The append-only rule as the screen expresses it. A rep is never offered an action that ends in
    // a 409.
    await logTable();

    expect(screen.queryByRole('button', { name: /edit/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /delete/i })).not.toBeInTheDocument();
  });

  it('logs a note through the customer’s own route', async () => {
    renderTab();

    await userEvent.click(await screen.findByRole('button', { name: 'Log a note' }));
    await userEvent.type(screen.getByLabelText('Note'), 'Rang about the reading.');
    await userEvent.click(screen.getByRole('button', { name: 'Log note' }));

    expect(stub.calls.filter((url) => url.pathname === notesPath).length).toBeGreaterThan(1);
  });

  it('refuses an empty note before it reaches the host', async () => {
    // Failure path. The host refuses it too; the duplication buys the rep the answer at the moment it
    // becomes wrong rather than as a 400 after they have pressed the button.
    renderTab();

    await userEvent.click(await screen.findByRole('button', { name: 'Log a note' }));

    const before = stub.calls.length;
    await userEvent.click(screen.getByRole('button', { name: 'Log note' }));

    expect(await screen.findByText('A note must say something.')).toBeInTheDocument();
    expect(stub.calls.length).toBe(before);
  });

  it('opens the CORRECTION form from a row, and says the original will survive it', async () => {
    const table = await logTable();

    await userEvent.click(table.getByText('Queried the consumption on the August bill.'));

    expect(await screen.findByText('Correct an earlier note')).toBeInTheDocument();

    // Said plainly, because it is the rule of this screen a rep is most likely to be surprised by.
    expect(
      screen.getByText(/The original stays on the log exactly as it was written/),
    ).toBeInTheDocument();

    // The words are pre-filled, because a rep correcting a note is usually fixing a few of them.
    expect(screen.getByLabelText('Note')).toHaveValue('Queried the consumption on the August bill.');
  });

  it('posts a correction to the NOTE’s own sub-resource, never a PUT of the note', async () => {
    const table = await logTable();

    await userEvent.click(table.getByText('Queried the consumption on the August bill.'));
    await userEvent.click(await screen.findByRole('button', { name: 'Record correction' }));

    expect(stub.lastCall(`/api/customer-notes/${dispute.id}/corrections`)).toBeDefined();
  });

  it('pins a note through the note’s own route', async () => {
    const table = await logTable();

    await userEvent.click(table.getByRole('button', { name: 'Pin this note to the top' }));

    expect(stub.lastCall(`/api/customer-notes/${dispute.id}/pin`)).toBeDefined();
  });

  it('offers to unpin the one that is already pinned', async () => {
    const table = await logTable();

    expect(table.getByRole('button', { name: 'Unpin this note' })).toBeInTheDocument();
  });

  it('renders an empty state rather than an error for a customer nobody has written about', async () => {
    // Failure path: "no notes" and "the register refused" are different answers, and only one of them
    // is a problem.
    renderTab(world((url) => (url.pathname === notesPath ? { body: [] } : undefined)));

    expect(await screen.findByText('Nothing logged yet')).toBeInTheDocument();
  });

  it('reports a refused log as a refusal, not as an empty log', async () => {
    // Failure path, and the distinction that matters: a rep without `customers.read` must not be
    // shown "Nothing logged yet", which would tell them this customer has never been rung. The shared
    // error state says what actually happened and does not offer a retry, because retrying a 403
    // gets another 403.
    renderTab(
      world((url) =>
        url.pathname === notesPath
          ? { status: 403, body: { title: 'Forbidden', status: 403, detail: 'customers.read required.' } }
          : undefined,
      ),
    );

    expect(await screen.findByText('You do not have access to this')).toBeInTheDocument();
    expect(screen.queryByText('Nothing logged yet')).not.toBeInTheDocument();
  });
});

describe('the pinned strip on the summary', () => {
  it('surfaces a pinned note above the customer record', async () => {
    // WORK_PACKAGES.md: "pinned notes surface at the top of the 360". A standing instruction is only
    // worth pinning if a rep meets it without going looking.
    renderTab(world(), `/customers/${record.id}`);

    expect(await screen.findByText('Pinned notes')).toBeInTheDocument();
    expect(screen.getByText('Dog on the property — sound the horn at the gate.')).toBeInTheDocument();
  });

  it('renders NOTHING when there is nothing pinned', async () => {
    // Not an empty state: that would be a permanent block of furniture on the page every customer
    // sees, explaining a feature to a rep who is trying to read a balance.
    renderTab(
      world((url) => (url.pathname === notesPath ? { body: [dispute] } : undefined)),
      `/customers/${record.id}`,
    );

    await screen.findByRole('heading', { name: 'Customer record' });

    expect(screen.queryByText('Pinned notes')).not.toBeInTheDocument();
  });
});
