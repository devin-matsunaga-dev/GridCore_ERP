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

export const api = {
  get: <T>(path: string, options?: RequestOptions) => request<T>('GET', path, options),
  post: <T>(path: string, options?: RequestOptions) => request<T>('POST', path, options),
  put: <T>(path: string, options?: RequestOptions) => request<T>('PUT', path, options),
  patch: <T>(path: string, options?: RequestOptions) => request<T>('PATCH', path, options),
  delete: <T>(path: string, options?: RequestOptions) => request<T>('DELETE', path, options),
};
