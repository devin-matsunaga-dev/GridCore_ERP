import { useQuery } from '@tanstack/react-query';
import { api } from './client';
import { registryWindow } from './registry';

/** The Assets module's client — the plant register and its maintenance history. */

/** Mirrors `GridCore.Modules.Assets.Features.Assets.AssetClass`. */
export const assetClasses = [
  'Transformer',
  'Pole',
  'ConductorSpan',
  'Switchgear',
  'Substation',
  'Generator',
  'Recloser',
  'Vehicle',
] as const;
export type AssetClass = (typeof assetClasses)[number];

/** Mirrors `AssetStatus`, in lifecycle order. */
export const assetStatuses = ['InStorage', 'InService', 'UnderMaintenance', 'Retired'] as const;
export type AssetStatus = (typeof assetStatuses)[number];

/** Mirrors `AssetCondition`, best to worst — the order an inspector reads it in. */
export const assetConditions = ['Unknown', 'Excellent', 'Good', 'Fair', 'Poor', 'Critical'] as const;
export type AssetCondition = (typeof assetConditions)[number];

/** Mirrors `AssetHistoryEntryType`. */
export const assetHistoryEntryTypes = ['Registered', 'StatusChanged', 'ConditionAssessed', 'Maintenance'] as const;
export type AssetHistoryEntryType = (typeof assetHistoryEntryTypes)[number];

/** Mirrors `AssetHistoryEntryResponse`. */
export type AssetHistoryEntry = {
  id: string;
  entryType: AssetHistoryEntryType;
  fromStatus: AssetStatus | null;
  toStatus: AssetStatus | null;
  fromCondition: AssetCondition | null;
  toCondition: AssetCondition | null;
  note: string | null;
  /** Work Orders is another module: this is a bare id until WP-3.4 gives a screen to resolve it. */
  workOrderId: string | null;
  actorId: string;
  actorName: string | null;
  recordedAt: string;
};

/** Mirrors `AssetResponse`. */
export type Asset = {
  id: string;
  assetTag: string;
  class: AssetClass;
  name: string;
  serialNumber: string | null;
  manufacturer: string | null;
  model: string | null;
  installedOn: string | null;
  status: AssetStatus;
  allowedTransitions: AssetStatus[];
  condition: AssetCondition;
  /** Both or neither — `GeoPosition` refuses a half-pair, so a latitude implies a longitude. */
  latitude: number | null;
  longitude: number | null;
  locationNote: string | null;
  registeredAt: string;
  statusChangedAt: string | null;
  statusReason: string | null;
  conditionAssessedAt: string | null;
  history: AssetHistoryEntry[];
};

export type AssetFilters = {
  search?: string;
  class?: AssetClass | '';
  status?: AssetStatus | '';
  condition?: AssetCondition | '';
};

function params(filters: Record<string, string | undefined>): Record<string, string> {
  return Object.fromEntries(
    Object.entries(filters).filter(([, value]) => value !== undefined && value !== ''),
  ) as Record<string, string>;
}

export const assetsApi = {
  list: (filters: AssetFilters, signal?: AbortSignal) =>
    api.get<Asset[]>('/api/assets', {
      query: { ...params({ ...filters }), limit: registryWindow },
      signal,
    }),
  get: (id: string, signal?: AbortSignal) => api.get<Asset>(`/api/assets/${id}`, { signal }),
  history: (id: string, entryType: AssetHistoryEntryType | '' | undefined, signal?: AbortSignal) =>
    api.get<AssetHistoryEntry[]>(`/api/assets/${id}/history`, {
      query: params({ entryType }),
      signal,
    }),
};

export const assetKeys = {
  all: ['assets'] as const,
  list: (filters: AssetFilters) => ['assets', 'list', filters] as const,
  detail: (id: string) => ['assets', 'detail', id] as const,
  history: (id: string, entryType: AssetHistoryEntryType | '' | undefined) =>
    ['assets', 'history', id, entryType ?? ''] as const,
};

export function useAssets(filters: AssetFilters) {
  return useQuery({
    queryKey: assetKeys.list(filters),
    queryFn: ({ signal }) => assetsApi.list(filters, signal),
  });
}

export function useAsset(id: string | undefined) {
  return useQuery({
    queryKey: assetKeys.detail(id ?? ''),
    queryFn: ({ signal }) => assetsApi.get(id!, signal),
    enabled: Boolean(id),
  });
}

/**
 * The maintenance history, narrowed by entry type. Its own query rather than the `history` the
 * detail response already carries, because the filter is the point — WP-3.4's maintenance lines
 * are what a technician came to read.
 */
export function useAssetHistory(id: string | undefined, entryType: AssetHistoryEntryType | '' = '') {
  return useQuery({
    queryKey: assetKeys.history(id ?? '', entryType),
    queryFn: ({ signal }) => assetsApi.history(id!, entryType, signal),
    enabled: Boolean(id),
  });
}
