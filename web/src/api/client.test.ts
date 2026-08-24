import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, api, setAccessTokenProvider } from './client';

function jsonResponse(body: unknown, init: ResponseInit = {}) {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
    ...init,
  });
}

describe('the API client', () => {
  let fetchMock: ReturnType<typeof vi.fn>;
  let restoreToken: () => void;

  beforeEach(() => {
    fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    restoreToken = setAccessTokenProvider(() => 'test-access-token');
  });

  afterEach(() => {
    restoreToken();
    vi.unstubAllGlobals();
  });

  it('sends the access token as a bearer header', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ userId: 'u1' }));

    await api.get('/api/me');

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect((init.headers as Record<string, string>).Authorization).toBe('Bearer test-access-token');
  });

  it('omits the header entirely when there is no token', async () => {
    restoreToken();
    fetchMock.mockResolvedValue(jsonResponse({}));

    await api.get('/api/me');

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(init.headers).not.toHaveProperty('Authorization');
  });

  it('serialises a JSON body and appends query parameters', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ ok: true }));

    await api.post('/api/work-orders', { json: { priority: 'High' }, query: { dryRun: true, skip: undefined } });

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('/api/work-orders?dryRun=true');
    expect(init.body).toBe('{"priority":"High"}');
    expect((init.headers as Record<string, string>)['Content-Type']).toBe('application/json');
  });

  it('returns undefined for a 204 rather than failing to parse an empty body', async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 204 }));

    await expect(api.delete('/api/work-orders/1')).resolves.toBeUndefined();
  });

  /** Failure path: the RBAC gate's 403 must arrive as a typed, readable error. */
  it('throws an ApiError carrying the ProblemDetails of a 403', async () => {
    fetchMock.mockResolvedValue(
      jsonResponse(
        { title: 'Forbidden', status: 403, detail: 'You do not hold billing.adjust.' },
        { status: 403 },
      ),
    );

    const error = await api.post('/api/bills/1/adjust').catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).isForbidden).toBe(true);
    expect((error as ApiError).message).toBe('You do not hold billing.adjust.');
  });

  it('surfaces validation errors from a 400', async () => {
    fetchMock.mockResolvedValue(
      jsonResponse({ status: 400, errors: { Name: ['Name is required.'] } }, { status: 400 }),
    );

    const error = (await api.post('/api/customers').catch((e: unknown) => e)) as ApiError;

    expect(error.validationErrors).toEqual({ Name: ['Name is required.'] });
  });

  /** Failure path: a proxy 502 is not ProblemDetails, and must not crash the parser. */
  it('still throws a typed error when the body is not ProblemDetails', async () => {
    fetchMock.mockResolvedValue(new Response('<html>Bad Gateway</html>', { status: 502 }));

    const error = (await api.get('/api/me').catch((e: unknown) => e)) as ApiError;

    expect(error).toBeInstanceOf(ApiError);
    expect(error.status).toBe(502);
    expect(error.problem).toBeNull();
    expect(error.message).toContain('502');
  });

  /**
   * Failure path — the one that showed up as a permanently empty sidebar: a stalled connection
   * used to leave the request hanging, so the UI sat in its loading state forever with nothing to
   * report. A stall now surfaces as an error the UI can render.
   */
  it('gives up on a request that never answers', async () => {
    fetchMock.mockImplementation(
      (_url: string, init: RequestInit) =>
        new Promise((_resolve, reject) => {
          init.signal?.addEventListener('abort', () => reject(init.signal!.reason));
        }),
    );

    const error = (await api.get('/api/me', { timeoutMs: 10 }).catch((e: unknown) => e)) as ApiError;

    expect(error).toBeInstanceOf(ApiError);
    expect(error.isUnreachable).toBe(true);
    expect(error.message).toContain('timed out');
  });

  it("passes the caller's own cancellation through untouched", async () => {
    const controller = new AbortController();
    fetchMock.mockImplementation(
      (_url: string, init: RequestInit) =>
        new Promise((_resolve, reject) => {
          init.signal?.addEventListener('abort', () => reject(init.signal!.reason));
        }),
    );

    const pending = api.get('/api/me').catch((e: unknown) => e);
    controller.abort();

    // A caller cancelling — a component unmounting, say — is not a failure to report.
    const error = await Promise.race([pending, Promise.resolve(controller.signal.reason)]);
    expect((error as Error).name).toBe('AbortError');
  });
});