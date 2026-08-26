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
  /**
   * Answered verbatim as `text/csv`, for a route that serves a file rather than JSON (WP-2.14's
   * payment-history export). Takes precedence over `body`, which is JSON-encoded — encoding a CSV
   * as JSON would hand the client a quoted string and the escaping under test would be the
   * encoder's rather than the host's.
   */
  text?: string;
};

export type FetchStub = {
  /** Every request the client made, in order. */
  calls: URL[];
  /** The most recent request whose path matches, for asserting on filters. */
  lastCall: (path: string) => URL | undefined;
  /**
   * The decoded JSON body of the most recent request to `path`, or `undefined` if it had none.
   *
   * Added for WP-2.15, where the URL alone is not the assertion: a transition sends the reason code
   * and the effective date in its body, and a form that dropped either would still hit exactly the
   * right route. Decoded here rather than in each test, so a body that is not JSON fails as one
   * clear undefined rather than as a parse error in a page test.
   */
  lastBody: (path: string) => unknown;
  restore: () => void;
};

/** A 404 in the host's own shape, for a path the test did not stub. */
const notFound: StubbedResponse = {
  status: 404,
  body: { title: 'Not found', status: 404, detail: 'No route stubbed for that request.' },
};

export function stubFetch(respond: (url: URL) => StubbedResponse | undefined): FetchStub {
  const calls: URL[] = [];
  const bodies: (unknown | undefined)[] = [];

  const spy = vi.spyOn(globalThis, 'fetch').mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
    const href =
      typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url;
    const url = new URL(href, 'http://localhost');
    calls.push(url);
    bodies.push(decode(init?.body));

    const answer = respond(url) ?? notFound;

    if (answer.text !== undefined) {
      return Promise.resolve(
        new Response(answer.text, {
          status: answer.status ?? 200,
          headers: { 'Content-Type': 'text/csv' },
        }),
      );
    }

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
    lastBody: (path) => {
      // Walked backwards over the index rather than `findLast`, because the answer is the body that
      // travelled WITH the matching call and the two arrays are parallel.
      for (let index = calls.length - 1; index >= 0; index -= 1) {
        if (calls[index].pathname === path) return bodies[index];
      }

      return undefined;
    },
    restore: () => spy.mockRestore(),
  };
}

/** The request body as the test wants to read it, or `undefined` for anything that is not JSON. */
function decode(body: BodyInit | null | undefined): unknown {
  if (typeof body !== 'string') return undefined;

  try {
    return JSON.parse(body) as unknown;
  } catch {
    return undefined;
  }
}

/** The shape a single stubbed route takes: an exact pathname and what to answer with. */
export function routes(table: Record<string, StubbedResponse>) {
  return (url: URL): StubbedResponse | undefined => table[url.pathname];
}
