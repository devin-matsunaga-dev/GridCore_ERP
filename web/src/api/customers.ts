import { useQueries, useQuery } from '@tanstack/react-query';
import { api } from './client';
import { registryWindow } from './registry';

/**
 * The Customers module's client — customers, the premises they are served at, and the service
 * accounts that pair the two. One typed client per module (CONVENTIONS.md); components never call
 * `fetch` and never build a URL themselves.
 */

/** Mirrors `GridCore.Modules.Customers.Features.Customers.CustomerClass`. */
export const customerClasses = ['Residential', 'Commercial'] as const;
export type CustomerClass = (typeof customerClasses)[number];

/** Mirrors `CustomerStatus`. The order is the lifecycle's, not the alphabet's. */
export const customerStatuses = ['Prospect', 'Active', 'Suspended', 'Closed'] as const;
export type CustomerStatus = (typeof customerStatuses)[number];

/** Mirrors `ServiceAccountStatus`. */
export const serviceAccountStatuses = ['Pending', 'Active', 'Disconnected', 'Closed'] as const;
export type ServiceAccountStatus = (typeof serviceAccountStatuses)[number];

/** Mirrors `CustomerResponse`. */
export type Customer = {
  id: string;
  accountNumber: string;
  name: string;
  contactName: string | null;
  email: string | null;
  phone: string | null;
  class: CustomerClass;
  status: CustomerStatus;
  /** What the aggregate would still allow — a UI renders these as the enabled transition buttons. */
  allowedTransitions: CustomerStatus[];
  depositHeld: number;
  registeredAt: string;
  statusChangedAt: string | null;
  statusReason: string | null;
};

/** Mirrors `AddressPayload`. */
export type Address = {
  line1: string;
  line2: string | null;
  city: string;
  region: string;
  country: string;
  postalCode: string | null;
};

/** Mirrors `ServiceLocationResponse`. */
export type ServiceLocation = {
  id: string;
  locationCode: string;
  address: Address;
  /** The server's one-line rendering — a table shows this rather than reassembling the parts. */
  formattedAddress: string;
  description: string | null;
  isActive: boolean;
  statusReason: string | null;
  registeredAt: string;
};

/** Mirrors `ServiceAccountHistoryEntryResponse`. */
export type ServiceAccountHistoryEntry = {
  id: string;
  fromStatus: ServiceAccountStatus | null;
  toStatus: ServiceAccountStatus;
  reason: string | null;
  actorId: string;
  actorName: string | null;
  recordedAt: string;
};

/** Mirrors `ServiceAccountResponse`. */
export type ServiceAccount = {
  id: string;
  accountNumber: string;
  customerId: string;
  serviceLocationId: string;
  status: ServiceAccountStatus;
  allowedTransitions: ServiceAccountStatus[];
  openedAt: string;
  serviceStartedAt: string | null;
  serviceEndedAt: string | null;
  statusChangedAt: string | null;
  statusReason: string | null;
  history: ServiceAccountHistoryEntry[];
};

export type CustomerFilters = {
  search?: string;
  status?: CustomerStatus | '';
  class?: CustomerClass | '';
};

export type ServiceLocationFilters = {
  search?: string;
  region?: string;
  /** `''` means "either" — the tri-state the region and status selects share. */
  isActive?: boolean | '';
};

export type ServiceAccountFilters = {
  search?: string;
  customerId?: string;
  serviceLocationId?: string;
  status?: ServiceAccountStatus | '';
};

/**
 * Drops the empty selections. `buildQuery` already skips `undefined`, but an empty string is a
 * value — sending `?status=` would be asking the host to parse `""` as a `CustomerStatus`, which
 * is a 400 rather than "no filter".
 */
function params(filters: Record<string, string | boolean | undefined>): Record<string, string | boolean> {
  return Object.fromEntries(
    Object.entries(filters).filter(([, value]) => value !== undefined && value !== ''),
  ) as Record<string, string | boolean>;
}

/** Mirrors `CreateCustomerRequest`. */
export type CreateCustomerInput = {
  name: string;
  class: CustomerClass;
  contactName?: string | null;
  email?: string | null;
  phone?: string | null;
  depositHeld?: number;
};

/** Mirrors `AddressPayload`. */
export type AddressInput = {
  line1: string;
  city: string;
  region: string;
  country: string;
  line2?: string | null;
  postalCode?: string | null;
};

/** Mirrors `ServiceLocationRequest`. */
export type CreateServiceLocationInput = {
  address: AddressInput;
  description?: string | null;
  isActive?: boolean;
};

/** Mirrors `OpenServiceAccountRequest`. */
export type OpenServiceAccountInput = {
  customerId: string;
  serviceLocationId: string;
  reason?: string | null;
};

/** Mirrors `DepositRuleResponse` — the class-based schedule, reference data on the host. */
export type DepositRule = {
  customerClass: CustomerClass;
  amount: number;
  description: string;
  ruleId: string;
};

/** Mirrors `NewPremiseRequest`. */
export type NewPremiseInput = {
  address: AddressInput;
  description?: string | null;
};

/** Mirrors `IntakePremiseRequest` — exactly one of the two, which the host refuses otherwise. */
export type IntakePremiseInput = {
  newPremise?: NewPremiseInput;
  serviceLocationId?: string;
};

/** Mirrors `RegisterCustomerIntakeRequest` — the wizard's single commit. */
export type CustomerIntakeInput = {
  name: string;
  class: CustomerClass;
  premise: IntakePremiseInput;
  contactName?: string | null;
  email?: string | null;
  phone?: string | null;
  depositCollected?: number;
  startService?: boolean;
  reason?: string | null;
};

/** Mirrors `DepositOutcomeResponse`. */
export type DepositOutcome = {
  customerClass: CustomerClass;
  assessedAmount: number;
  collectedAmount: number;
  ruleId: string;
};

/** Mirrors `CustomerMatchKind`. The order is match precedence, not the alphabet's. */
export const customerMatchKinds = ['AccountNumber', 'MeterNumber', 'Phone', 'Name', 'Address'] as const;
export type CustomerMatchKind = (typeof customerMatchKinds)[number];

/**
 * Mirrors `CustomerSearchHitResponse` — one result row and why it is one.
 *
 * The customer arrives whole, in the shape `GET /api/customers` returns them, which is what lets
 * the registry table render a search result and a registry row with the same columns.
 */
export type CustomerSearchHit = {
  customer: Customer;
  matchedOn: CustomerMatchKind;
  isExact: boolean;
  /** The stored value that matched, as stored — never a normalised form. */
  matchedValue: string;
  serviceAccountCount: number;
  serviceAccountNumber: string | null;
  serviceAddress: string | null;
  meterNumber: string | null;
};

/** Mirrors `CustomerSearchResponse` — a page of results and what the host made of the term. */
export type CustomerSearchResult = {
  term: string;
  kinds: CustomerMatchKind[];
  hits: CustomerSearchHit[];
  /** Matching customers across every page — the host ranks before it pages, so this is a real count. */
  total: number;
  page: number;
  pageSize: number;
  /** A candidate cap was reached, so `total` is a floor rather than a count. */
  truncated: boolean;
};

/** Mirrors `CustomerRegistrationResponse` — everything one intake produced. */
export type CustomerRegistration = {
  customer: Customer;
  location: ServiceLocation;
  /** False when the intake opened the account at a premise already on the books. */
  locationWasRegistered: boolean;
  account: ServiceAccount;
  deposit: DepositOutcome;
};

export const customersApi = {
  list: (filters: CustomerFilters, signal?: AbortSignal) =>
    api.get<Customer[]>('/api/customers', {
      query: { ...params({ ...filters }), limit: registryWindow },
      signal,
    }),
  get: (id: string, signal?: AbortSignal) => api.get<Customer>(`/api/customers/${id}`, { signal }),

  /**
   * The CSR search box (WP-2.9) — what the registry's own search field runs when it has a term in
   * it. Takes the same status and class filters as `list`, because it sits beside the same selects.
   *
   * Asks for one `registryWindow` of ranked rows and lets `useTableState` sort and page it in the
   * browser, exactly as every other registry does. The endpoint can page on the server and the
   * ranking is a whole-result-set operation either way; asking for the window keeps one code path
   * through the table card, and `truncated`/`isWindowFull` are what keep the screen honest when the
   * answer did not fit.
   */
  search: (filters: CustomerFilters, signal?: AbortSignal) =>
    api.get<CustomerSearchResult>('/api/customers/search', {
      query: { q: filters.search ?? '', ...params({ status: filters.status, class: filters.class }), pageSize: registryWindow },
      signal,
    }),

  listLocations: (filters: ServiceLocationFilters, signal?: AbortSignal) =>
    api.get<ServiceLocation[]>('/api/service-locations', {
      query: { ...params({ ...filters }), limit: registryWindow },
      signal,
    }),
  getLocation: (id: string, signal?: AbortSignal) =>
    api.get<ServiceLocation>(`/api/service-locations/${id}`, { signal }),

  listAccounts: (filters: ServiceAccountFilters, signal?: AbortSignal) =>
    api.get<ServiceAccount[]>('/api/service-accounts', {
      query: { ...params({ ...filters }), limit: registryWindow },
      signal,
    }),
  getAccount: (id: string, signal?: AbortSignal) =>
    api.get<ServiceAccount>(`/api/service-accounts/${id}`, { signal }),

  /**
   * One account's transitions, on their own.
   *
   * A LIST ROW CARRIES NO HISTORY — `ServiceAccountService.ListAsync` includes none, deliberately:
   * a list shows where an account stands, not how it got there. So a screen that wants the record
   * an agent reads back on the phone asks for it per account, which is what this endpoint is for.
   */
  accountHistory: (id: string, signal?: AbortSignal) =>
    api.get<ServiceAccountHistoryEntry[]>(`/api/service-accounts/${id}/history`, { signal }),

  // The writes. Registering a customer, a premise and the account that pairs them are three acts
  // rather than one form, because they are three registries — and the revenue cycle is only a
  // cycle if each of them is a step somebody can see happen.
  create: (input: CreateCustomerInput) => api.post<Customer>('/api/customers', { json: input }),

  createLocation: (input: CreateServiceLocationInput) =>
    api.post<ServiceLocation>('/api/service-locations', { json: input }),

  openAccount: (input: OpenServiceAccountInput) =>
    api.post<ServiceAccount>('/api/service-accounts', { json: input }),

  /**
   * Energises an account. A separate act from opening one, and the billing run refuses an account
   * that was never energised — nothing was supplied, so the units on the meter are not its units.
   */
  startService: (id: string, reason?: string) =>
    api.post<ServiceAccount>(`/api/service-accounts/${id}/start`, { json: { reason } }),

  /** The deposit schedule. Reference data on the host, so it is safe to cache for a session. */
  depositRules: (signal?: AbortSignal) => api.get<DepositRule[]>('/api/deposit-rules', { signal }),

  /**
   * The intake wizard's one commit (WP-2.8). Deliberately NOT the three calls above in sequence:
   * the customer, the premise and the account are written in a single host-side transaction, so a
   * wizard abandoned or refused part-way leaves nothing behind.
   */
  register: (input: CustomerIntakeInput) =>
    api.post<CustomerRegistration>('/api/customer-registrations', { json: input }),
};

export const customerKeys = {
  all: ['customers'] as const,
  depositRules: () => ['deposit-rules'] as const,
  list: (filters: CustomerFilters) => ['customers', 'list', filters] as const,
  search: (filters: CustomerFilters) => ['customers', 'search', filters] as const,
  detail: (id: string) => ['customers', 'detail', id] as const,
  locations: (filters: ServiceLocationFilters) => ['service-locations', 'list', filters] as const,
  location: (id: string) => ['service-locations', 'detail', id] as const,
  accounts: (filters: ServiceAccountFilters) => ['service-accounts', 'list', filters] as const,
  account: (id: string) => ['service-accounts', 'detail', id] as const,
  accountHistory: (id: string) => ['service-accounts', 'history', id] as const,
};

/**
 * The deposit schedule. Reference data — it changes by migration, never by a screen — so it is held
 * for the session rather than re-fetched as the wizard's class select moves.
 */
export function useDepositRules() {
  return useQuery({
    queryKey: customerKeys.depositRules(),
    queryFn: ({ signal }) => customersApi.depositRules(signal),
    staleTime: Infinity,
  });
}

/**
 * The registry list. Takes `enabled` because the registry screen runs this or `useCustomerSearch`
 * and never both — an empty search field lists customers, a term in it searches them.
 */
export function useCustomers(filters: CustomerFilters, enabled = true) {
  return useQuery({
    queryKey: customerKeys.list(filters),
    queryFn: ({ signal }) => customersApi.list(filters, signal),
    enabled,
  });
}

/**
 * The registry's search field, once it has something in it.
 *
 * Disabled on an empty term, which is what makes the registry page's pair of queries an either/or
 * rather than two requests per keystroke: no term means the plain list answers, a term means this
 * one does. `placeholderData` keeps the previous rows on screen while the next answer loads, so the
 * table does not blank out between keystrokes.
 */
export function useCustomerSearch(filters: CustomerFilters, enabled: boolean) {
  return useQuery({
    queryKey: customerKeys.search(filters),
    queryFn: ({ signal }) => customersApi.search(filters, signal),
    enabled,
    placeholderData: (previous) => previous,
  });
}

export function useCustomer(id: string | undefined) {
  return useQuery({
    queryKey: customerKeys.detail(id ?? ''),
    queryFn: ({ signal }) => customersApi.get(id!, signal),
    enabled: Boolean(id),
  });
}

export function useServiceLocations(filters: ServiceLocationFilters) {
  return useQuery({
    queryKey: customerKeys.locations(filters),
    queryFn: ({ signal }) => customersApi.listLocations(filters, signal),
  });
}

export function useServiceAccounts(filters: ServiceAccountFilters, enabled = true) {
  return useQuery({
    queryKey: customerKeys.accounts(filters),
    queryFn: ({ signal }) => customersApi.listAccounts(filters, signal),
    enabled,
  });
}

/**
 * The transitions of a set of accounts, one query each — the shape `useServiceLocationsByIds`
 * established, for the same reason and with the same failure it avoids.
 *
 * This is not an optimisation of the list: **the list genuinely has no history on it**, because
 * `ServiceAccountService.ListAsync` includes none. A screen that reads `account.history` off a
 * list row gets an empty array in the running app whatever the test fixture says, and shows
 * nothing where a service record should be. WP-2.10's timeline needs the transitions, so it asks
 * for them.
 */
export function useServiceAccountHistories(ids: readonly string[]) {
  const unique = [...new Set(ids)];

  return useQueries({
    queries: unique.map((id) => ({
      queryKey: customerKeys.accountHistory(id),
      queryFn: ({ signal }: { signal: AbortSignal }) => customersApi.accountHistory(id, signal),
      staleTime: 60_000,
    })),
    combine: (results) => ({
      isPending: results.some((result) => result.isPending),
      /** Keyed by account, so a card and the timeline look their entries up the same way. */
      byAccountId: new Map(
        unique
          .map((id, index) => [id, results[index]?.data] as const)
          .filter((pair): pair is readonly [string, ServiceAccountHistoryEntry[]] => pair[1] !== undefined),
      ),
    }),
  });
}

/**
 * The premises a set of accounts is served at, one query each rather than a filtered list.
 * A customer holds a handful of accounts, so this is a handful of cached-by-id requests — and it
 * cannot silently miss a premise the way indexing a capped list page would.
 */
export function useServiceLocationsByIds(ids: readonly string[]) {
  const unique = [...new Set(ids)];

  return useQueries({
    queries: unique.map((id) => ({
      queryKey: customerKeys.location(id),
      queryFn: ({ signal }: { signal: AbortSignal }) => customersApi.getLocation(id, signal),
      staleTime: 60_000,
    })),
    combine: (results) => ({
      isPending: results.some((result) => result.isPending),
      byId: new Map(
        results
          .map((result) => result.data)
          .filter((location): location is ServiceLocation => location !== undefined)
          .map((location) => [location.id, location]),
      ),
    }),
  });
}
