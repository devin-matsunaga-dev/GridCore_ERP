import { afterEach, describe, expect, it } from 'vitest';
import { meterStatuses, meterTypes, meteringApi } from './metering';
import { registryWindow } from './registry';
import { stubFetch, type FetchStub } from '@/test/api-stub';

let stub: FetchStub;

afterEach(() => stub?.restore());

describe('meteringApi', () => {
  it('sends only the filters that were actually chosen', async () => {
    stub = stubFetch(() => ({ body: [] }));

    await meteringApi.list({ search: 'MTR-0000', status: '', type: 'ThreePhase', fitted: '' });

    const query = stub.lastCall('/api/meters')!.searchParams;

    // An empty select means "no filter" and is dropped: `?status=` would ask the host to parse ""
    // as a MeterStatus, which is a 400 rather than "everything".
    expect(query.get('search')).toBe('MTR-0000');
    expect(query.get('type')).toBe('ThreePhase');
    expect(query.has('status')).toBe(false);
    expect(query.has('fitted')).toBe(false);
  });

  it('keeps a false toggle, which is a real filter rather than an empty one', async () => {
    stub = stubFetch(() => ({ body: [] }));

    await meteringApi.list({ fitted: false });

    // "Meters in a store" is a question; only '' means "either".
    expect(stub.lastCall('/api/meters')!.searchParams.get('fitted')).toBe('false');
  });

  it('asks for the registry window, because the host reports no total', async () => {
    stub = stubFetch(() => ({ body: [] }));

    await meteringApi.list({});

    expect(stub.lastCall('/api/meters')!.searchParams.get('limit')).toBe(String(registryWindow));
  });

  it('filters by premise, which is the only way a meter relates to anything else', async () => {
    stub = stubFetch(() => ({ body: [] }));

    await meteringApi.list({ serviceLocationId: '0192f000-0000-7000-8000-000000000101' });

    expect(stub.lastCall('/api/meters')!.searchParams.get('serviceLocationId')).toBe(
      '0192f000-0000-7000-8000-000000000101',
    );
  });

  it('mirrors the host enums it filters on', () => {
    // These are the strings the host parses. A rename on either side that is not made on both is a
    // 400 the moment somebody picks that option, so the lists are pinned rather than inferred.
    expect(meterTypes).toEqual(['SinglePhase', 'ThreePhase', 'CurrentTransformer', 'Demand']);
    expect(meterStatuses).toEqual(['InStore', 'Installed', 'Faulty', 'Removed', 'Retired']);
  });
});
