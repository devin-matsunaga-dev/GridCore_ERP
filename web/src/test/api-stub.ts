import { vi } from 'vitest';

/**
 * Stubs `fetch` so a page test drives the real API client — query-string building, ProblemDetails
 * parsing and all. Asserting on the URL the client produced is the point: a filter that silently
 * stopped reaching the server would otherwise still look right on screen.
 *
 * Nothing here starts a server; the fast tier never touches the network (CONVENTIONS.md ⚡).
 */

export type StubbedResponse = {
  /** Defaults to 200. */
  status?: number;
  body?: unknown;
};

export type FetchStub = {
  /** Every request the client made, in order. */
  calls: URL[];
  /** The most recent request whose path matches, for asserting on filters. */
  lastCall: (path: string) => URL | undefined;
  restore: () => void;
};

/** A 404 in the host's own shape, for a path the test did not stub. */
const notFound: StubbedResponse = {
  status: 404,
  body: { title: 'Not found', status: 404, detail: 'No route stubbed for that request.' },
};

export function stubFetch(respond: (url: URL) => StubbedResponse | undefined): FetchStub {
  const calls: URL[] = [];

  const spy = vi.spyOn(globalThis, 'fetch').mockImplementation((input: RequestInfo | URL) => {
    const href =
      typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url;
    const url = new URL(href, 'http://localhost');
    calls.push(url);

    const answer = respond(url) ?? notFound;

    return Promise.resolve(
      new Response(JSON.stringify(answer.body ?? null), {
        status: answer.status ?? 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    );
  });

  return {
    calls,
    lastCall: (path) => calls.findLast((url) => url.pathname === path),
    restore: () => spy.mockRestore(),
  };
}

/** The shape a single stubbed route takes: an exact pathname and what to answer with. */
export function routes(table: Record<string, StubbedResponse>) {
  return (url: URL): StubbedResponse | undefined => table[url.pathname];
}
