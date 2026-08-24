import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { ServiceLocationsPage } from './service-locations-page';
import { stubFetch, type FetchStub, type StubbedResponse } from '@/test/api-stub';
import { serviceAccount, serviceLocation } from '@/test/registry-fixtures';
import { renderWithProviders } from '@/test/render';

const songsong = serviceLocation();
const retired = serviceLocation({
  id: '0192f000-0000-7000-8000-000000000103',
  locationCode: 'L-000003',
  formattedAddress: '3 Sabana Road, Sinapalo, Rota, MP 96951',
  address: { ...serviceLocation().address, line1: '3 Sabana Road', city: 'Sinapalo' },
  isActive: false,
  statusReason: 'Structure demolished',
  description: null,
});

let stub: FetchStub;

afterEach(() => stub?.restore());

function world(url: URL): StubbedResponse | undefined {
  if (url.pathname === '/api/service-locations') return { body: [songsong, retired] };
  if (url.pathname === '/api/service-accounts') return { body: [serviceAccount()] };
  return undefined;
}

function renderPage(respond: (url: URL) => StubbedResponse | undefined = world) {
  stub = stubFetch(respond);

  return renderWithProviders(<ServiceLocationsPage />, { route: '/customers/locations' });
}

describe('ServiceLocationsPage', () => {
  it('lists premises with their island', async () => {
    renderPage();

    expect(await screen.findByText('12 Songsong Village Road')).toBeInTheDocument();
    expect(screen.getByText('3 Sabana Road')).toBeInTheDocument();
    expect(screen.getAllByText('Rota').length).toBeGreaterThan(0);
  });

  /** The three main Northern Marianas are the whole territory, so they are the filter's options. */
  it('offers the three islands as the region filter', async () => {
    renderPage();
    await screen.findByText('12 Songsong Village Road');

    const islands = screen.getByLabelText('Island');
    expect(within(islands).getAllByRole('option').map((option) => option.textContent)).toEqual([
      'All islands',
      'Saipan',
      'Rota',
      'Tinian',
    ]);

    await userEvent.selectOptions(islands, 'Tinian');
    await waitFor(() => {
      expect(stub.lastCall('/api/service-locations')?.searchParams.get('region')).toBe('Tinian');
    });
  });

  /** `?isActive=` is a boolean on the wire; the select's three states have to survive the trip. */
  it('translates the availability select into the boolean the host expects', async () => {
    renderPage();
    await screen.findByText('12 Songsong Village Road');

    await userEvent.selectOptions(screen.getByLabelText('Availability'), 'Inactive');
    await waitFor(() => {
      expect(stub.lastCall('/api/service-locations')?.searchParams.get('isActive')).toBe('false');
    });

    await userEvent.selectOptions(screen.getByLabelText('Availability'), 'Active');
    await waitFor(() => {
      expect(stub.lastCall('/api/service-locations')?.searchParams.get('isActive')).toBe('true');
    });

    await userEvent.selectOptions(screen.getByLabelText('Availability'), 'Active and inactive');
    await waitFor(() => {
      expect(stub.lastCall('/api/service-locations')?.searchParams.has('isActive')).toBe(false);
    });
  });

  it('opens a drawer showing the premise and the accounts held there', async () => {
    renderPage();
    await screen.findByText('12 Songsong Village Road');

    await userEvent.click(screen.getByRole('button', { name: /12 Songsong Village Road/ }));

    const drawer = await screen.findByRole('dialog');
    expect(within(drawer).getByText('Songsong')).toBeInTheDocument();
    expect(within(drawer).getByText('96951')).toBeInTheDocument();
    expect(await within(drawer).findByText('A-000001')).toBeInTheDocument();
    expect(stub.lastCall('/api/service-accounts')?.searchParams.get('serviceLocationId')).toBe(
      songsong.id,
    );
  });

  /** A premise nobody has ever opened an account at is free — the drawer says so rather than blank. */
  it('says a premise is free when no account has ever been opened there', async () => {
    renderPage((url) => {
      if (url.pathname === '/api/service-locations') return { body: [songsong] };
      if (url.pathname === '/api/service-accounts') return { body: [] };
      return undefined;
    });
    await screen.findByText('12 Songsong Village Road');

    await userEvent.click(screen.getByRole('button', { name: /12 Songsong Village Road/ }));

    const drawer = await screen.findByRole('dialog');
    expect(await within(drawer).findByText(/this premise is free/i)).toBeInTheDocument();
  });

  /** Failure path: a deactivated premise shows why, because that is the only record of it. */
  it('shows why a premise was deactivated', async () => {
    renderPage();
    await screen.findByText('3 Sabana Road');

    await userEvent.click(screen.getByRole('button', { name: /3 Sabana Road/ }));

    const drawer = await screen.findByRole('dialog');
    expect(within(drawer).getByText('Deactivated because')).toBeInTheDocument();
    expect(within(drawer).getByText('Structure demolished')).toBeInTheDocument();
  });
});
