import { env } from '@/lib/env';

/**
 * The one place the SPA talks HTTP. Components never call `fetch` (CONVENTIONS.md) — they use a
 * per-module typed client built on this, through TanStack Query.
 */

/** RFC 7807 ProblemDetails, the only error shape the host returns. */
export type ProblemDetails = {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  errors?: Record<string, string[]>;
};

/** A non-2xx response, carrying the ProblemDetails body when the host sent one. */
export class ApiError extends Error {
  readonly status: number;
  readonly problem: ProblemDetails | null;

  constructor(status: number, problem: ProblemDetails | null, fallbackMessage: string) {
    super(problem?.detail ?? problem?.title ?? fallbackMessage);
    this.name = 'ApiError';
    this.status = status;
    this.problem = problem;
  }

  /** True when the caller is signed out or their token expired. */
  get isUnauthenticated(): boolean {
    return this.status === 401;
  }

  /** True when the caller is signed in but lacks the permission — the 403 the RBAC gate returns. */
  get isForbidden(): boolean {
    return this.status === 403;
  }

  /** True when no HTTP status came back at all — the request timed out or never connected. */
  get isUnreachable(): boolean {
    return this.status === 0;
  }

  /** Field-level validation messages, flattened for a form. */
  get validationErrors(): Record<string, string[]> {
    return this.problem?.errors ?? {};
  }
}

type AccessTokenProvider = () => string | undefined;

let accessTokenProvider: AccessTokenProvider = () => undefined;

/**
 * Lets the auth layer hand the client a token getter without the client importing React. Returns a
 * disposer, so a test can restore the default with the value it got back.
 */
export function setAccessTokenProvider(provider: AccessTokenProvider): () => void {
  accessTokenProvider = provider;
  return clearAccessTokenProvider;
}

/** Forgets the current token getter. Every later request goes out unauthenticated. */
export function clearAccessTokenProvider(): void {
  accessTokenProvider = () => undefined;
}

/**
 * How long a request may hang before it is treated as failed. Without this a stalled connection —
 * a dev proxy pointing at a host that never answers, say — leaves the UI in its loading state
 * forever, which reads as a broken screen rather than an error anyone can act on.
 */
export const requestTimeoutMs = 15_000;

function isTimeout(error: unknown): boolean {
  return typeof error === 'object' && error !== null && (error as { name?: string }).name === 'TimeoutError';
}

/** Combines the caller's cancellation with the timeout, so whichever fires first wins. */
function abortSignalFor(callerSignal: AbortSignal | undefined, timeoutMs: number): AbortSignal {
  const timeout = AbortSignal.timeout(timeoutMs);

  return callerSignal ? AbortSignal.any([callerSignal, timeout]) : timeout;
}

export type RequestOptions = Omit<RequestInit, 'body' | 'method'> & {
  /** Serialised as JSON. Use `RequestInit.body` directly for anything else. */
  json?: unknown;
  query?: Record<string, string | number | boolean | undefined | null>;
  signal?: AbortSignal;
  /** Overrides {@link requestTimeoutMs} for one call — a long report, say. */
  timeoutMs?: number;
};

async function request<TResponse>(
  method: string,
  path: string,
  { json, query, headers, signal, timeoutMs = requestTimeoutMs, ...init }: RequestOptions = {},
): Promise<TResponse> {
  const token = accessTokenProvider();
  const url = `${env.apiBaseUrl}${path}${buildQuery(query)}`;

  let response: Response;
  try {
    response = await fetch(url, {
      ...init,
      method,
      signal: abortSignalFor(signal, timeoutMs),
      headers: {
        Accept: 'application/json',
        ...(json === undefined ? {} : { 'Content-Type': 'application/json' }),
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
        ...headers,
      },
      body: json === undefined ? undefined : JSON.stringify(json),
    });
  } catch (error) {
    // Matched on `name`, not `instanceof DOMException`: the exception crosses realms (jsdom's
    // DOMException is not Node's), and the name is the part the platform guarantees. The caller
    // cancelling raises `AbortError` and is passed through — that is not a failure to report.
    if (isTimeout(error)) {
      throw new ApiError(0, null, `${method} ${path} timed out after ${timeoutMs}ms`);
    }
    throw error;
  }

  if (!response.ok) {
    throw new ApiError(response.status, await readProblem(response), `${method} ${path} failed (${response.status})`);
  }

  return (await readBody<TResponse>(response))!;
}

async function readProblem(response: Response): Promise<ProblemDetails | null> {
  try {
    const body = (await response.json()) as ProblemDetails;
    return typeof body === 'object' && body !== null ? body : null;
  } catch {
    // A proxy or gateway error is not ProblemDetails; the status still tells the caller enough.
    return null;
  }
}

async function readBody<T>(response: Response): Promise<T | undefined> {
  if (response.status === 204 || response.headers.get('Content-Length') === '0') {
    return undefined;
  }
  const text = await response.text();
  return text.length === 0 ? undefined : (JSON.parse(text) as T);
}

function buildQuery(query: RequestOptions['query']): string {
  if (!query) return '';
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value !== undefined && value !== null) params.append(key, String(value));
  }
  const serialised = params.toString();
  return serialised.length === 0 ? '' : `?${serialised}`;
}

/**
 * A GET whose answer is a file rather than JSON — WP-2.14's payment-history export, and so far the
 * only one.
 *
 * Shares every other rule of {@link request}: the bearer token, the timeout, and a non-2xx turning
 * into an {@link ApiError} carrying whatever ProblemDetails the host sent. That last part is why
 * this lives here rather than in the module client — a 403 on an export is the same 403 as
 * everywhere else, and a second `fetch` in the codebase would be a second place to get it wrong.
 *
 * The host serves the file with a UTF-8 byte-order mark, which `Response.text()` strips as it
 * decodes. A caller writing the text back out to a file has to put one back; `downloadCsv` is what
 * does that.
 */
async function requestText(path: string, { query, headers, signal, timeoutMs = requestTimeoutMs, ...init }: RequestOptions = {}): Promise<string> {
  const token = accessTokenProvider();
  const url = `${env.apiBaseUrl}${path}${buildQuery(query)}`;

  let response: Response;
  try {
    response = await fetch(url, {
      ...init,
      method: 'GET',
      signal: abortSignalFor(signal, timeoutMs),
      headers: {
        Accept: 'text/csv, text/plain',
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
        ...headers,
      },
    });
  } catch (error) {
    if (isTimeout(error)) {
      throw new ApiError(0, null, `GET ${path} timed out after ${timeoutMs}ms`);
    }
    throw error;
  }

  if (!response.ok) {
    throw new ApiError(response.status, await readProblem(response), `GET ${path} failed (${response.status})`);
  }

  return await response.text();
}

/**
 * A POST whose body is a file — WP-2.18's application documents, and so far the only one.
 *
 * Shares every other rule of {@link request}: the bearer token, the timeout, and a non-2xx turning
 * into an {@link ApiError} carrying whatever ProblemDetails the host sent. What it does NOT do is
 * set `Content-Type`: a multipart body needs a boundary parameter that only the browser knows, and
 * setting the header by hand is the way to produce a request the server cannot parse.
 *
 * A longer timeout than the default, because this one is carrying megabytes over whatever
 * connection a counter has rather than a few hundred bytes of JSON.
 */
async function requestForm<TResponse>(
  path: string,
  body: FormData,
  { headers, signal, timeoutMs = uploadTimeoutMs, ...init }: Omit<RequestOptions, 'json'> = {},
): Promise<TResponse> {
  const token = accessTokenProvider();
  const url = `${env.apiBaseUrl}${path}`;

  let response: Response;
  try {
    response = await fetch(url, {
      ...init,
      method: 'POST',
      signal: abortSignalFor(signal, timeoutMs),
      headers: {
        Accept: 'application/json',
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
        ...headers,
      },
      body,
    });
  } catch (error) {
    if (isTimeout(error)) {
      throw new ApiError(0, null, `POST ${path} timed out after ${timeoutMs}ms`);
    }
    throw error;
  }

  if (!response.ok) {
    throw new ApiError(response.status, await readProblem(response), `POST ${path} failed (${response.status})`);
  }

  return (await readBody<TResponse>(response))!;
}

/** How long an upload may take before it is treated as failed. A scan is not a JSON body. */
export const uploadTimeoutMs = 60_000;

export const api = {
  get: <T>(path: string, options?: RequestOptions) => request<T>('GET', path, options),
  getText: (path: string, options?: RequestOptions) => requestText(path, options),
  post: <T>(path: string, options?: RequestOptions) => request<T>('POST', path, options),
  postForm: <T>(path: string, body: FormData, options?: Omit<RequestOptions, 'json'>) =>
    requestForm<T>(path, body, options),
  put: <T>(path: string, options?: RequestOptions) => request<T>('PUT', path, options),
  patch: <T>(path: string, options?: RequestOptions) => request<T>('PATCH', path, options),
  delete: <T>(path: string, options?: RequestOptions) => request<T>('DELETE', path, options),
};
