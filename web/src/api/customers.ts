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

export const customersApi = {
  list: (filters: CustomerFilters, signal?: AbortSignal) =>
    api.get<Customer[]>('/api/customers', {
      query: { ...params({ ...filters }), limit: registryWindow },
      signal,
    }),
  get: (id: string, signal?: AbortSignal) => api.get<Customer>(`/api/customers/${id}`, { signal }),

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
};

export const customerKeys = {
  all: ['customers'] as const,
  list: (filters: CustomerFilters) => ['customers', 'list', filters] as const,
  detail: (id: string) => ['customers', 'detail', id] as const,
  locations: (filters: ServiceLocationFilters) => ['service-locations', 'list', filters] as const,
  location: (id: string) => ['service-locations', 'detail', id] as const,
  accounts: (filters: ServiceAccountFilters) => ['service-accounts', 'list', filters] as const,
  account: (id: string) => ['service-accounts', 'detail', id] as const,
};

export function useCustomers(filters: CustomerFilters) {
  return useQuery({
    queryKey: customerKeys.list(filters),
    queryFn: ({ signal }) => customersApi.list(filters, signal),
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
