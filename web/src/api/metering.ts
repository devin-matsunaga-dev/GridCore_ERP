import { useQueries, useQuery } from '@tanstack/react-query';
import { api } from './client';
import { registryWindow } from './registry';

/**
 * The Metering module's client — the utility's revenue meters and where each of them is fitted.
 * One typed client per module (CONVENTIONS.md); components never call `fetch` and never build a
 * URL themselves.
 */

/** Mirrors `GridCore.Modules.Metering.Features.Meters.MeterType`. */
export const meterTypes = ['SinglePhase', 'ThreePhase', 'CurrentTransformer', 'Demand'] as const;
export type MeterType = (typeof meterTypes)[number];

/** Mirrors `MeterStatus`. The order is the lifecycle's, not the alphabet's. */
export const meterStatuses = ['InStore', 'Installed', 'Faulty', 'Removed', 'Retired'] as const;
export type MeterStatus = (typeof meterStatuses)[number];

/** Mirrors `MeterHistoryEntryType`. */
export const meterHistoryEntryTypes = ['Registered', 'Installed', 'Removed', 'StatusChanged'] as const;
export type MeterHistoryEntryType = (typeof meterHistoryEntryTypes)[number];

/**
 * Mirrors `MeterServiceLocationResponse` — the premise as the meter register reports it, resolved
 * for us by the host through the Customers module. A read-only copy on the wire, never a join.
 */
export type MeterServiceLocation = {
  id: string;
  locationCode: string;
  formattedAddress: string;
  isActive: boolean;
};

/** Mirrors `MeterHistoryEntryResponse`. */
export type MeterHistoryEntry = {
  id: string;
  entryType: MeterHistoryEntryType;
  fromStatus: MeterStatus | null;
  toStatus: MeterStatus;
  /** The premise involved, on an installation or a removal line. */
  serviceLocationId: string | null;
  note: string | null;
  actorId: string;
  actorName: string | null;
  recordedAt: string;
};

/** Mirrors `MeterResponse`. */
export type Meter = {
  id: string;
  meterNumber: string;
  serialNumber: string;
  type: MeterType;
  manufacturer: string | null;
  model: string | null;
  /** Whole digits the register carries before the dials roll back to zero. */
  registerDigits: number;
  /** What that register counts up to before it returns to zero. */
  registerCapacity: number;
  status: MeterStatus;
  /** Whether it is on a premise and measuring supply. */
  isFitted: boolean;
  /** Every status the machine allows from here. */
  allowedTransitions: MeterStatus[];
  /**
   * The subset reachable through the status endpoint. Fitting and unfitting are `assign` and
   * `remove`, so buttons come from this list — the full one would offer moves that always 409.
   */
  allowedStatusChanges: MeterStatus[];
  serviceLocationId: string | null;
  serviceLocation: MeterServiceLocation | null;
  installedAt: string | null;
  installationReading: number | null;
  registeredAt: string;
  statusChangedAt: string | null;
  statusReason: string | null;
  /** Empty on a list row; the detail endpoint fills it in. */
  history: MeterHistoryEntry[];
};

export type MeterFilters = {
  search?: string;
  type?: MeterType | '';
  status?: MeterStatus | '';
  serviceLocationId?: string;
  /** `''` means "either" — a meter on a premise, one in a store, or both. */
  fitted?: boolean | '';
};

/**
 * Drops the empty selections, exactly as the Customers client does. `buildQuery` already skips
 * `undefined`, but an empty string is a value — sending `?status=` would ask the host to parse `""`
 * as a `MeterStatus`, which is a 400 rather than "no filter".
 */
function params(filters: Record<string, string | boolean | undefined>): Record<string, string | boolean> {
  return Object.fromEntries(
    Object.entries(filters).filter(([, value]) => value !== undefined && value !== ''),
  ) as Record<string, string | boolean>;
}

export const meteringApi = {
  list: (filters: MeterFilters, signal?: AbortSignal) =>
    api.get<Meter[]>('/api/meters', {
      query: { ...params({ ...filters }), limit: registryWindow },
      signal,
    }),
  get: (id: string, signal?: AbortSignal) => api.get<Meter>(`/api/meters/${id}`, { signal }),
};

export const meterKeys = {
  all: ['meters'] as const,
  list: (filters: MeterFilters) => ['meters', 'list', filters] as const,
  detail: (id: string) => ['meters', 'detail', id] as const,
  atLocation: (serviceLocationId: string) => ['meters', 'at-location', serviceLocationId] as const,
};

export function useMeters(filters: MeterFilters) {
  return useQuery({
    queryKey: meterKeys.list(filters),
    queryFn: ({ signal }) => meteringApi.list(filters, signal),
  });
}

/**
 * The meter fitted at each of a set of premises — one query per premise rather than one filtered
 * list, the shape `useServiceLocationsByIds` established: a customer holds a handful of premises,
 * so this is a handful of cached-by-premise requests, and it cannot silently miss one the way
 * indexing a capped list page could.
 *
 * At most one meter comes back per premise, because at most one may be fitted there
 * (`ux_meters_service_location`). This is how the 360° page derives "the meter on this account" —
 * through the **location**, never through the account, because a meter is fitted to a place and
 * has no account of its own.
 */
export function useMetersByLocationIds(ids: readonly string[]) {
  const unique = [...new Set(ids)];

  return useQueries({
    queries: unique.map((serviceLocationId) => ({
      queryKey: meterKeys.atLocation(serviceLocationId),
      queryFn: ({ signal }: { signal: AbortSignal }) =>
        meteringApi.list({ serviceLocationId }, signal),
      staleTime: 60_000,
    })),
    combine: (results) => ({
      isPending: results.some((result) => result.isPending),
      /** Keyed by premise, so a card looks its meter up by the premise it is served at. */
      byLocationId: new Map(
        results
          .map((result) => result.data?.[0])
          .filter((meter): meter is Meter & { serviceLocationId: string } =>
            typeof meter?.serviceLocationId === 'string')
          .map((meter) => [meter.serviceLocationId, meter]),
      ),
    }),
  });
}
