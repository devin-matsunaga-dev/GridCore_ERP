import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { AssetsPage } from './assets-page';
import { stubFetch, type FetchStub, type StubbedResponse } from '@/test/api-stub';
import { asset, assetHistoryEntry } from '@/test/registry-fixtures';
import { renderWithProviders } from '@/test/render';

const transformer = asset();
const pole = asset({
  id: '0192f000-0000-7000-8000-000000000402',
  assetTag: 'AST-000002',
  class: 'Pole',
  name: 'Sinapalo Road pole 118',
  serialNumber: null,
  manufacturer: null,
  model: null,
  status: 'UnderMaintenance',
  condition: 'Poor',
  installedOn: null,
  latitude: null,
  longitude: null,
  allowedTransitions: ['InService', 'InStorage', 'Retired'],
});

let stub: FetchStub;

afterEach(() => stub?.restore());

function world(url: URL): StubbedResponse | undefined {
  if (url.pathname === '/api/assets') return { body: [transformer, pole] };
  if (url.pathname === `/api/assets/${transformer.id}`) return { body: transformer };
  if (url.pathname === `/api/assets/${transformer.id}/history`) {
    return { body: [assetHistoryEntry({ entryType: 'Registered', toStatus: 'InStorage', note: null }), assetHistoryEntry()] };
  }
  return undefined;
}

function renderPage(respond: (url: URL) => StubbedResponse | undefined = world) {
  stub = stubFetch(respond);

  return renderWithProviders(<AssetsPage />, { route: '/assets' });
}

describe('AssetsPage', () => {
  it('lists the register', async () => {
    renderPage();

    expect(await screen.findByText('Songsong pole-top transformer')).toBeInTheDocument();
    expect(screen.getByText('Sinapalo Road pole 118')).toBeInTheDocument();
    expect(screen.getByText('AST-000001')).toBeInTheDocument();
  });

  it('reads the class name in sentence case without changing the value it filters on', async () => {
    renderPage();
    await screen.findByText('Songsong pole-top transformer');

    // `ConductorSpan` reads "Conductor span" but posts back as the host's own enum name.
    const classFilter = screen.getByLabelText('Class');
    expect(within(classFilter).getByRole('option', { name: 'Conductor span' })).toHaveValue('ConductorSpan');

    await userEvent.selectOptions(classFilter, 'ConductorSpan');
    await waitFor(() => {
      expect(stub.lastCall('/api/assets')?.searchParams.get('class')).toBe('ConductorSpan');
    });
  });

  it('sends the condition filter to the host', async () => {
    renderPage();
    await screen.findByText('Songsong pole-top transformer');

    await userEvent.selectOptions(screen.getByLabelText('Condition'), 'Critical');
    await waitFor(() => {
      expect(stub.lastCall('/api/assets')?.searchParams.get('condition')).toBe('Critical');
    });
  });

  /** Plant with no serial (poles, spans) must sink, not head the register when reversed. */
  it('sorts assets without a serial number to the bottom either way', async () => {
    renderPage();
    await screen.findByText('Songsong pole-top transformer');

    await userEvent.click(screen.getByRole('button', { name: /serial/i }));
    expect(lastRowText()).toContain('Sinapalo Road pole 118');

    await userEvent.click(screen.getByRole('button', { name: /serial/i }));
    expect(lastRowText()).toContain('Sinapalo Road pole 118');
  });

  it('opens a drawer for the activated row and closes it again', async () => {
    renderPage();
    await screen.findByText('Songsong pole-top transformer');

    await userEvent.click(screen.getByRole('button', { name: /Songsong pole-top transformer/ }));

    const drawer = await screen.findByRole('dialog', { name: 'Songsong pole-top transformer' });
    expect(within(drawer).getByText('Hitachi')).toBeInTheDocument();
    expect(within(drawer).getByText('TX-88213')).toBeInTheDocument();
    // Both-or-neither: one row, not a latitude that could sit alone.
    expect(within(drawer).getByText('14.142000, 145.185000')).toBeInTheDocument();

    await userEvent.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  /**
   * The maintenance history is fetched with `?entryType=`, so narrowing it is a request — which is
   * how WP-3.4's maintenance lines will arrive here with no change to this screen.
   */
  it('narrows the asset history on the server', async () => {
    renderPage();
    await screen.findByText('Songsong pole-top transformer');
    await userEvent.click(screen.getByRole('button', { name: /Songsong pole-top transformer/ }));
    await screen.findByRole('dialog');

    await userEvent.selectOptions(screen.getByLabelText('History entry type'), 'Maintenance');

    await waitFor(() => {
      expect(
        stub.lastCall(`/api/assets/${transformer.id}/history`)?.searchParams.get('entryType'),
      ).toBe('Maintenance');
    });
  });

  it('describes each kind of history line', async () => {
    renderPage();
    await screen.findByText('Songsong pole-top transformer');
    await userEvent.click(screen.getByRole('button', { name: /Songsong pole-top transformer/ }));

    const drawer = await screen.findByRole('dialog');
    expect(within(drawer).getByText('Entered in the register')).toBeInTheDocument();
    expect(within(drawer).getByText('Assessed excellent → good')).toBeInTheDocument();
  });

  /** Failure path: a filter that matches nothing says so, and offers a way back. */
  it('shows a filtered empty state', async () => {
    renderPage((url) => (url.pathname === '/api/assets' ? { body: [] } : undefined));
    await screen.findByText('Nothing in the register yet');

    await userEvent.type(screen.getByLabelText('Search assets'), 'nothing');

    expect(await screen.findByText('No plant matches those filters')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Clear filters' })).toBeInTheDocument();
  });
});

/** The last body row's text — the header row is dropped. */
function lastRowText(): string {
  const [, ...body] = within(screen.getByRole('table', { name: 'Assets' })).getAllByRole('row');

  return body.at(-1)?.textContent ?? '';
}
