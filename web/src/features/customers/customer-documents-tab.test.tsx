import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { CustomerDetailPage } from './customer-detail-page';
import { stubFetch, type FetchStub, type StubbedResponse } from '@/test/api-stub';
import { accountStatement, customer, statementEntry } from '@/test/registry-fixtures';
import { bill } from '@/test/revenue-cycle-fixtures';
import { renderWithProviders } from '@/test/render';

/**
 * The documents tab (WP-2.14): the statement, the export, and the way in to a bill reprint.
 *
 * Its own file, the split the contacts and deposit tabs already made. Everything drives the real API
 * client through a stubbed `fetch`, so what is asserted includes the URLs — and on this tab the URLs
 * are half the point: producing a statement and exporting a history are AUDITED on the host, so a
 * tab that fetched one by being opened would put an entry in the trail saying a document went to a
 * customer who never asked for one.
 */

const record = customer();
const statementPath = `/api/customers/${record.id}/documents/statement`;
const exportPath = `/api/customers/${record.id}/documents/payment-history`;

let stub: FetchStub;

const noObjectUrls = {
  createObjectURL: URL.createObjectURL,
  revokeObjectURL: URL.revokeObjectURL,
};

afterEach(() => {
  stub?.restore();

  URL.createObjectURL = noObjectUrls.createObjectURL;
  URL.revokeObjectURL = noObjectUrls.revokeObjectURL;
});

const issued = bill({ id: 'bill-issued', billNumber: 'BIL-000001', status: 'Issued', issuedOn: '2026-07-05' });
const draft = bill({ id: 'bill-draft', billNumber: 'BIL-000002', status: 'Draft', issuedOn: null });

/** The 360 with its documents tab answered, and every other panel empty. */
function world(overrides: (url: URL) => StubbedResponse | undefined = () => undefined) {
  return (url: URL): StubbedResponse | undefined => {
    const override = overrides(url);
    if (override) return override;

    if (url.pathname === `/api/customers/${record.id}`) return { body: record };
    if (url.pathname === statementPath) return { body: accountStatement() };
    if (url.pathname === exportPath) return { text: 'Payment number,Customer\r\nPAY-000001,"Cruz, Ana"\r\n' };
    if (url.pathname === '/api/bills') return { body: [issued, draft] };
    if (url.pathname === `/api/customers/${record.id}/contacts`) return { body: [] };
    if (url.pathname === `/api/customers/${record.id}/profile`) return { body: null };
    if (url.pathname === `/api/customers/${record.id}/deposits`) return { body: null };
    if (url.pathname === `/api/customers/${record.id}/notes`) return { body: [] };
    if (url.pathname === '/api/service-accounts') return { body: [] };
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
    { route: `/customers/${record.id}/documents` },
  );
}

describe('the documents tab', () => {
  it('is a route, so the tab a rep pastes is the tab that opens', async () => {
    renderTab();

    expect(await screen.findByRole('heading', { name: 'Account statement' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Customer record' })).not.toBeInTheDocument();
  });

  it('produces NOTHING until a rep asks for it', async () => {
    // THE CLAIM THIS TAB IS BUILT AROUND. Every other panel on the 360 fetches at the page, because
    // switching tabs should issue no request. Here the request is an audited act, so opening the tab
    // must not make one.
    renderTab();

    await screen.findByRole('heading', { name: 'Account statement' });

    expect(stub.calls.some((url) => url.pathname === statementPath)).toBe(false);
    expect(stub.calls.some((url) => url.pathname === exportPath)).toBe(false);
  });

  it('asks for the range in the two boxes when the button is pressed', async () => {
    renderTab();

    await userEvent.click(await screen.findByRole('button', { name: 'Produce statement' }));

    const asked = await vi.waitFor(() => {
      const call = stub.lastCall(statementPath);
      expect(call).toBeDefined();
      return call!;
    });

    // The boxes open on the last quarter — the host's own default, restated so the two selects show
    // the range the screen is about to produce rather than nothing.
    expect(asked.searchParams.get('from')).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    expect(asked.searchParams.get('to')).toMatch(/^\d{4}-\d{2}-\d{2}$/);
  });

  it('renders the statement from an opening balance to a closing balance', async () => {
    renderTab(
      world((url) =>
        url.pathname === statementPath
          ? {
              body: accountStatement({
                openingBalance: 100,
                closingBalance: 145,
                entries: [
                  statementEntry({ amount: 120, balanceAfter: 220 }),
                  statementEntry({
                    kind: 'PaymentReceived',
                    description: 'Payment PAY-000001 received',
                    reference: 'PAY-000001',
                    billId: null,
                    date: '2026-07-20',
                    occurredAt: '2026-07-20T09:00:00+00:00',
                    amount: -75,
                    balanceAfter: 145,
                  }),
                ],
                billed: 120,
                paid: 75,
              }),
            }
          : undefined,
      ),
    );

    await userEvent.click(await screen.findByRole('button', { name: 'Produce statement' }));

    const statement = within(await screen.findByRole('table'));

    expect(statement.getByText('Opening balance')).toBeInTheDocument();
    expect(statement.getByText('$100.00')).toBeInTheDocument();
    expect(statement.getByText('Closing balance')).toBeInTheDocument();
    expect(statement.getAllByText('$145.00').length).toBeGreaterThan(0);

    // And the two kinds of line read as what they are.
    expect(statement.getByText('Bill issued')).toBeInTheDocument();
    expect(statement.getByText('Payment received')).toBeInTheDocument();
  });

  it('says so, loudly, when a statement does not add up', async () => {
    // The host refuses to compose one, so this can only arrive if the two sides disagree about what
    // a line means. A document about money that quietly does not add up is worse than one that says
    // it does not.
    renderTab(
      world((url) =>
        url.pathname === statementPath ? { body: accountStatement({ closingBalance: 999 }) } : undefined,
      ),
    );

    await userEvent.click(await screen.findByRole('button', { name: 'Produce statement' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/does not add up and must not be sent/);
  });

  it('warns when the history behind the opening balance did not fit', async () => {
    renderTab(
      world((url) =>
        url.pathname === statementPath ? { body: accountStatement({ isTruncated: true }) } : undefined,
      ),
    );

    await userEvent.click(await screen.findByRole('button', { name: 'Produce statement' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/opening balance may be\s+short/);
  });

  it('reads as a carried-across balance when nothing happened in the period', async () => {
    // Not an error and not an empty state. "You owed 120, nothing happened, you owe 120" is a
    // complete answer, and it is the one a customer chasing a missing bill is ringing about.
    renderTab(
      world((url) =>
        url.pathname === statementPath
          ? { body: accountStatement({ entries: [], openingBalance: 120, closingBalance: 120, billed: 0 }) }
          : undefined,
      ),
    );

    await userEvent.click(await screen.findByRole('button', { name: 'Produce statement' }));

    expect(await screen.findByText(/Nothing happened on this account in this period/)).toBeInTheDocument();
  });

  it('refuses to ask for a range that runs backwards', async () => {
    // Caught in the browser, so a rep is not told by a 400 what the two boxes in front of them
    // already said. The host refuses it too.
    renderTab();

    await userEvent.clear(await screen.findByLabelText('From'));
    await userEvent.type(screen.getByLabelText('From'), '2026-12-31');

    expect(screen.getByRole('button', { name: 'Produce statement' })).toBeDisabled();
    expect(stub.calls.some((url) => url.pathname === statementPath)).toBe(false);
  });

  it('exports the payment history as a file the browser saves', async () => {
    // The two statics are replaced rather than the whole `URL`, which jsdom does not implement.
    // Replacing the global outright would take `new URL()` with it — and the API client builds every
    // request URL with it, so the export would fail before it reached the download.
    const blobs: Blob[] = [];

    URL.createObjectURL = ((blob: Blob) => {
      blobs.push(blob);
      return 'blob:stub';
    }) as typeof URL.createObjectURL;
    URL.revokeObjectURL = (() => {}) as typeof URL.revokeObjectURL;

    renderTab();

    await userEvent.click(await screen.findByRole('button', { name: 'Export payment history' }));

    await vi.waitFor(() => expect(blobs).toHaveLength(1));

    // The host's escaping reaches the file intact — it is fetched as text rather than JSON, so a
    // name with a comma in it is still one column.
    const bytes = new Uint8Array(await blobs[0].arrayBuffer());

    expect(new TextDecoder().decode(bytes)).toContain('"Cruz, Ana"');
    expect(stub.lastCall(exportPath)).toBeDefined();
  });

  it('offers a reprint for every issued bill and none for a draft', async () => {
    renderTab();

    const reprints = within(
      (await screen.findByRole('heading', { name: 'Bill reprints' })).closest('[data-slot="card"]')!,
    );

    // A link rather than a button: the document has its own URL, so a rep can send a colleague the
    // exact bill they are looking at.
    expect(reprints.getByRole('link', { name: /BIL-000001/ })).toHaveAttribute(
      'href',
      `/customers/${record.id}/bills/${issued.id}`,
    );

    // A DRAFT is not a document anybody was sent, and the host answers a 409 for one.
    expect(reprints.queryByText('BIL-000002')).not.toBeInTheDocument();
  });
});
