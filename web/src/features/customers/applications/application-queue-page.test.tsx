import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { stubFetch, type FetchStub, type StubbedResponse } from '@/test/api-stub';
import { applicationDocument, applicationReference, serviceApplication, serviceAccount } from '@/test/registry-fixtures';
import { renderWithProviders } from '@/test/render';
import { ApplicationQueuePage } from './application-queue-page';

/**
 * The review desk (WP-2.18): the queue, and the drawer a reviewer works an application in.
 *
 * Everything drives the real API client through a stubbed `fetch`, so the URL and the body each act
 * produced are part of what is asserted — a decision that stopped reaching the host would otherwise
 * still look right on screen, because the drawer re-renders from the refetch either way.
 */

const queued = serviceApplication();

const underReview = serviceApplication({
  id: '0192f000-0000-7000-8000-000000000602',
  applicationNumber: 'AP-000002',
  status: 'UnderReview',
  allowedTransitions: ['Approved', 'Rejected', 'Withdrawn'],
  reviewStartedAt: '2026-08-27T09:35:00+00:00',
  reviewerName: 'Ana Cruz (demo)',
  documents: [applicationDocument(), applicationDocument({ id: 'doc-2', kind: 'ProofOfOccupancy', fileName: 'lease.pdf' })],
  checklist: [
    { kind: 'PhotoId', isSatisfied: true, documentId: applicationDocument().id, uploadedAt: '2026-08-27T09:40:00+00:00' },
    { kind: 'ProofOfOccupancy', isSatisfied: true, documentId: 'doc-2', uploadedAt: '2026-08-27T09:41:00+00:00' },
  ],
  missingDocuments: [],
  isDocumentationComplete: true,
});

let stub: FetchStub;

afterEach(() => stub?.restore());

/** The desk with its queue answered and the host's reference data in place. */
function world(
  applications: unknown[] = [queued, underReview],
  overrides: (url: URL) => StubbedResponse | undefined = () => undefined,
) {
  return (url: URL): StubbedResponse | undefined => {
    const override = overrides(url);
    if (override) return override;

    if (url.pathname === '/api/service-applications') return { body: applications };
    if (url.pathname === '/api/service-application-reference') return { body: applicationReference() };

    return undefined;
  };
}

function renderQueue(respond: (url: URL) => StubbedResponse | undefined = world()) {
  stub = stubFetch(respond);

  return renderWithProviders(<ApplicationQueuePage />, { route: '/customers/applications' });
}

describe('the application queue', () => {
  it('opens on what is waiting, not on every application ever filed', async () => {
    renderQueue();

    expect(await screen.findByText('AP-000001')).toBeInTheDocument();

    // The desk's question is "what is waiting for me". `openOnly` is what asks it.
    expect(stub.lastCall('/api/service-applications')?.searchParams.get('openOnly')).toBe('true');
  });

  it('drops the queue filter once a named status is chosen, because the two would fight', async () => {
    // Asking for Approved AND still-open is a query that can only ever be empty.
    renderQueue();

    await screen.findByText('AP-000001');
    await userEvent.selectOptions(screen.getByLabelText('Status'), 'Approved');

    const last = stub.lastCall('/api/service-applications')!;

    expect(last.searchParams.get('status')).toBe('Approved');
    expect(last.searchParams.get('openOnly')).toBeNull();
  });

  it('shows how far along the checklist each application is', async () => {
    renderQueue();

    const queuedRow = (await screen.findByText('AP-000001')).closest('tr')!;

    expect(within(queuedRow).getByText('0 of 2')).toBeInTheDocument();
    expect(within(queuedRow).getByText('Submitted')).toBeInTheDocument();
  });

  it('offers no decision on an application nobody has picked up, and picks it up on request', async () => {
    renderQueue();

    await userEvent.click(await screen.findByRole('button', { name: /AP-000001/ }));

    // WP-2.18's whole point: reviewed before it establishes an account.
    expect(screen.queryByLabelText('Outcome')).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Start review' }));

    expect(stub.lastCall(`/api/service-applications/${queued.id}/review`)).toBeDefined();
  });

  it('records an approval with its reason code, and says what the approval opened', async () => {
    renderQueue(
      world([queued, underReview], (url) =>
        url.pathname === `/api/service-applications/${underReview.id}/approve`
          ? {
              body: {
                application: { ...underReview, status: 'Approved', isOpen: false },
                account: serviceAccount({ accountNumber: 'A-000042', status: 'Pending' }),
                deposit: { currency: 'USD', shortfallAmount: 75, requiredAmount: 75, heldAmount: 0, accounts: [] },
              },
            }
          : undefined,
      ),
    );

    await userEvent.click(await screen.findByRole('button', { name: /AP-000002/ }));

    await userEvent.selectOptions(screen.getByLabelText('Outcome'), 'Approved');
    await userEvent.selectOptions(screen.getByLabelText('Reason'), 'DocumentsVerified');
    await userEvent.click(screen.getByRole('button', { name: /Record approved/i }));

    expect(stub.lastBody(`/api/service-applications/${underReview.id}/approve`)).toMatchObject({
      reasonCode: 'DocumentsVerified',
    });
  });

  it('will not let an incomplete application be approved without an exception that says why', async () => {
    const incomplete = serviceApplication({
      id: '0192f000-0000-7000-8000-000000000603',
      applicationNumber: 'AP-000003',
      status: 'UnderReview',
      allowedTransitions: ['Approved', 'Rejected', 'Withdrawn'],
      missingDocuments: ['ProofOfOccupancy'],
      isDocumentationComplete: false,
    });

    renderQueue(world([incomplete]));

    await userEvent.click(await screen.findByRole('button', { name: /AP-000003/ }));

    await userEvent.selectOptions(screen.getByLabelText('Outcome'), 'Approved');
    await userEvent.selectOptions(screen.getByLabelText('Reason'), 'DocumentsVerified');

    expect(screen.getByRole('button', { name: /Record approved/i })).toBeDisabled();
    expect(screen.getAllByText(/Lease or deed/).length).toBeGreaterThan(0);

    // The escape hatch opens the button — and then demands a sentence of its own.
    await userEvent.selectOptions(screen.getByLabelText('Reason'), 'ApprovedByException');
    expect(screen.getByRole('button', { name: /Record approved/i })).toBeDisabled();

    await userEvent.type(screen.getByLabelText('Notes'), 'Government premise, rebuild after the storm.');
    expect(screen.getByRole('button', { name: /Record approved/i })).toBeEnabled();
  });

  it('clears the reason when the outcome changes, because the lists do not overlap', async () => {
    renderQueue(world([underReview]));

    await userEvent.click(await screen.findByRole('button', { name: /AP-000002/ }));

    await userEvent.selectOptions(screen.getByLabelText('Outcome'), 'Rejected');
    await userEvent.selectOptions(screen.getByLabelText('Reason'), 'IdentityNotVerified');
    await userEvent.selectOptions(screen.getByLabelText('Outcome'), 'Approved');

    expect(screen.getByLabelText('Reason')).toHaveValue('');
    expect(screen.getByRole('button', { name: /Record approved/i })).toBeDisabled();
  });

  it('links each attached document at the route the host serves its bytes from', async () => {
    renderQueue(world([underReview]));

    await userEvent.click(await screen.findByRole('button', { name: /AP-000002/ }));

    const links = screen.getAllByRole('link', { name: 'View' });

    expect(links[0]).toHaveAttribute(
      'href',
      expect.stringContaining(`/api/service-applications/${underReview.id}/documents/${applicationDocument().id}/content`),
    );
  });

  it('offers a fresh application on a rejected one rather than a way to reopen it', async () => {
    const rejected = serviceApplication({
      id: '0192f000-0000-7000-8000-000000000604',
      applicationNumber: 'AP-000004',
      status: 'Rejected',
      allowedTransitions: [],
      isOpen: false,
      decidedAt: '2026-08-27T11:00:00+00:00',
      decisionReasonCode: 'OccupancyNotProven',
    });

    renderQueue(world([rejected]));

    await userEvent.click(await screen.findByRole('button', { name: /AP-000004/ }));

    expect(screen.queryByLabelText('Outcome')).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'File a fresh application' }));

    expect(stub.lastCall(`/api/service-applications/${rejected.id}/resubmissions`)).toBeDefined();
  });

  it('renders a refusal as an error rather than as an empty queue', async () => {
    renderQueue(() => ({
      status: 403,
      body: { title: 'Not permitted', status: 403, detail: 'You do not have permission to do that.' },
    }));

    expect(await screen.findByText(/do not have permission/i)).toBeInTheDocument();
  });
});
