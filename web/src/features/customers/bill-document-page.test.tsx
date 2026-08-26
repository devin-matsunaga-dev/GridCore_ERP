import { screen, within } from '@testing-library/react';
import { Route, Routes } from 'react-router';
import { afterEach, describe, expect, it } from 'vitest';
import { BillDocumentPage } from './bill-document-page';
import { stubFetch, type FetchStub, type StubbedResponse } from '@/test/api-stub';
import { customer } from '@/test/registry-fixtures';
import { billDocument, billTotal } from '@/test/revenue-cycle-fixtures';
import { renderWithProviders } from '@/test/render';

/**
 * The bill reprint (WP-2.14).
 *
 * The claim under test is the one a customer would dispute: **the document reproduces the bill as it
 * was issued**, and the credit granted since is a separate dated row rather than a smaller number in
 * the consumption line. A reprint that netted the two would be a bill that has never existed.
 */

const record = customer();
const document = billDocument();
const documentPath = `/api/bills/${document.billId}/document`;

let stub: FetchStub;

afterEach(() => stub?.restore());

function renderPage(respond: (url: URL) => StubbedResponse | undefined = () => ({ body: document })) {
  stub = stubFetch(respond);

  return renderWithProviders(
    <Routes>
      <Route path="/customers/:customerId/bills/:billId" element={<BillDocumentPage />} />
    </Routes>,
    { route: `/customers/${record.id}/bills/${document.billId}` },
  );
}

describe('the bill reprint', () => {
  it('reproduces the bill as it was issued', async () => {
    renderPage();

    expect(await screen.findByRole('heading', { name: `Bill ${document.billNumber}` })).toBeInTheDocument();

    const charges = within(screen.getByRole('table', { name: 'What the bill charged, as issued' }));

    expect(charges.getByText('Total as issued')).toBeInTheDocument();
    expect(charges.getByText(`$${billTotal.toFixed(2)}`)).toBeInTheDocument();
  });

  it('shows a correction BENEATH the document rather than inside it', async () => {
    // WP-2.4's rule read forwards. The printed total keeps saying what the customer holds a copy of;
    // the credit is its own dated entry with the reason on it.
    renderPage();

    expect(await screen.findByRole('heading', { name: 'Corrections since this bill was issued' })).toBeInTheDocument();
    expect(screen.getByText('Meter misread')).toBeInTheDocument();
    expect(screen.getByText('-$10.00')).toBeInTheDocument();

    // And the charges table still adds up to the printed total, untouched.
    const charges = within(screen.getByRole('table', { name: 'What the bill charged, as issued' }));

    expect(charges.getByText(`$${billTotal.toFixed(2)}`)).toBeInTheDocument();
  });

  it('names the customer the bill was BILLED to', async () => {
    // Not the customer's name today. A customer who has since married still had this bill sent to
    // the name printed on it, and a reprint that quietly updated it would be a different document.
    renderPage(() => ({ body: billDocument({ customerName: 'Sablan Family Residence (as billed)' }) }));

    expect(await screen.findByText('Sablan Family Residence (as billed)')).toBeInTheDocument();
  });

  it('says who produced the copy and when, because it left the building', async () => {
    renderPage();

    expect(await screen.findByText(/Copy produced .* by Ana Cruz \(demo\)/)).toBeInTheDocument();
  });

  it('asks the host for the document exactly once', async () => {
    // Producing a copy is AUDITED. A page that refetched on focus would put a second "a copy went
    // out" entry in the trail because somebody switched tabs.
    renderPage();

    await screen.findByRole('heading', { name: `Bill ${document.billNumber}` });

    expect(stub.calls.filter((url) => url.pathname === documentPath)).toHaveLength(1);
  });

  it('reads a refused reprint as a refusal rather than an empty document', async () => {
    // The 403 a caller without `customers.documents` gets, and the 409 a draft gets, both land here.
    renderPage(() => ({
      status: 403,
      body: { title: 'Not permitted', status: 403, detail: 'Producing a copy of a bill requires the customers.documents permission.' },
    }));

    expect(await screen.findByText(/requires the customers.documents permission/)).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('offers the way back to the documents tab it was opened from', async () => {
    renderPage();

    expect(await screen.findByRole('link', { name: 'Back to documents' })).toHaveAttribute(
      'href',
      `/customers/${record.id}/documents`,
    );
  });
});
