import { useState } from 'react';
import {
  assetHistoryEntryTypes,
  useAsset,
  useAssetHistory,
  type Asset,
  type AssetHistoryEntry,
  type AssetHistoryEntryType,
} from '@/api/assets';
import { DetailList, orNotRecorded } from '@/components/registry/detail-list';
import { Drawer, DrawerSection } from '@/components/registry/drawer';
import { FilterSelect } from '@/components/registry/filter-bar';
import { Timeline, type TimelineEntry } from '@/components/registry/timeline';
import { Skeleton } from '@/components/ui/skeleton';
import { StatusPill, toneFor } from '@/components/ui/status';
import { formatDate, formatLabel } from '@/lib/format';

/**
 * A piece of plant's detail, with its maintenance history. A drawer: a technician checking what a
 * transformer is and when it was last inspected should not lose the filtered register behind them.
 *
 * The history is its own request rather than the `history` the row already carries, because the
 * `?entryType=` filter is what a maintenance planner came for — and WP-3.4's maintenance lines will
 * arrive on that endpoint without this screen changing.
 */
export function AssetDrawer({ assetId, onClose }: { assetId: string | null; onClose: () => void }) {
  const [entryType, setEntryType] = useState<AssetHistoryEntryType | ''>('');

  const asset = useAsset(assetId ?? undefined);
  const history = useAssetHistory(assetId ?? undefined, entryType);

  if (!assetId) return null;

  return (
    <Drawer
      open
      onClose={onClose}
      title={asset.data?.name ?? 'Loading asset…'}
      subtitle={
        asset.data && (
          <>
            <span className="text-muted tabular text-[13px]">{asset.data.assetTag}</span>
            <StatusPill status={formatLabel(asset.data.status)} />
            <StatusPill status={formatLabel(asset.data.condition)} tone={toneFor(asset.data.condition)} />
          </>
        )
      }
    >
      {asset.isPending || !asset.data ? (
        <div className="space-y-3">
          <Skeleton className="h-4 w-full" />
          <Skeleton className="h-4 w-2/3" />
          <Skeleton className="h-32 w-full" />
        </div>
      ) : (
        <div className="space-y-6">
          <DrawerSection title="Plant">
            <DetailList items={plantItems(asset.data)} />
          </DrawerSection>

          <DrawerSection title="Where it is">
            <DetailList items={locationItems(asset.data)} />
          </DrawerSection>

          <DrawerSection title="Lifecycle">
            <div className="space-y-4">
              <DetailList
                items={[
                  { label: 'Status changed', value: orNotRecorded(asset.data.statusChangedAt && formatDate(asset.data.statusChangedAt)) },
                  { label: 'Condition assessed', value: orNotRecorded(asset.data.conditionAssessedAt && formatDate(asset.data.conditionAssessedAt)) },
                  { label: 'Status reason', value: orNotRecorded(asset.data.statusReason), wide: true },
                ]}
              />
              <div>
                <p className="text-muted text-[11px] font-medium tracking-[0.06em] uppercase">
                  Allowed transitions
                </p>
                <div className="mt-2 flex flex-wrap gap-1.5">
                  {asset.data.allowedTransitions.length === 0 ? (
                    <span className="text-muted text-[13px]">None — retired is terminal.</span>
                  ) : (
                    asset.data.allowedTransitions.map((status) => (
                      <StatusPill key={status} status={formatLabel(status)} tone={toneFor(status)} />
                    ))
                  )}
                </div>
              </div>
            </div>
          </DrawerSection>

          <DrawerSection
            title="Maintenance history"
            action={
              <FilterSelect
                label="History entry type"
                anyLabel="Everything"
                value={entryType}
                onChange={setEntryType}
                options={assetHistoryEntryTypes}
                format={formatLabel}
              />
            }
          >
            {history.isPending ? (
              <Skeleton className="h-24 w-full" />
            ) : history.data && history.data.length > 0 ? (
              <Timeline entries={history.data.map(toTimelineEntry).toReversed()} />
            ) : (
              <p className="text-muted text-[13px]">
                {entryType === ''
                  ? 'Nothing recorded against this asset yet.'
                  : `No ${formatLabel(entryType).toLowerCase()} entries on this asset.`}
              </p>
            )}
          </DrawerSection>
        </div>
      )}
    </Drawer>
  );
}

function plantItems(asset: Asset) {
  return [
    { label: 'Class', value: formatLabel(asset.class) },
    { label: 'Installed', value: orNotRecorded(asset.installedOn && formatDate(asset.installedOn)) },
    { label: 'Manufacturer', value: orNotRecorded(asset.manufacturer) },
    { label: 'Model', value: orNotRecorded(asset.model) },
    { label: 'Serial number', value: orNotRecorded(asset.serialNumber) },
    { label: 'Registered', value: formatDate(asset.registeredAt) },
  ];
}

/**
 * Latitude and longitude are both-or-neither — `GeoPosition` refuses a half-pair — so the position
 * is one row rather than two that could disagree. An asset stands at a coordinate and a note, never
 * at a service location: a span crosses several premises and a bucket truck is wherever it parked.
 */
function locationItems(asset: Asset) {
  return [
    {
      label: 'Position',
      wide: true,
      value:
        asset.latitude !== null && asset.longitude !== null ? (
          <span className="tabular">
            {asset.latitude.toFixed(6)}, {asset.longitude.toFixed(6)}
          </span>
        ) : (
          orNotRecorded(null)
        ),
    },
    { label: 'Location note', value: orNotRecorded(asset.locationNote), wide: true },
  ];
}

/** One history line, however it was written: a transition, a grade, or WP-3.4's completed job. */
function toTimelineEntry(entry: AssetHistoryEntry): TimelineEntry {
  return {
    id: entry.id,
    title: describe(entry),
    detail: entry.note,
    actor: entry.actorName ?? entry.actorId,
    recordedAt: entry.recordedAt,
    tone: toneFor(entry.toCondition ?? entry.toStatus ?? entry.entryType),
  };
}

function describe(entry: AssetHistoryEntry): string {
  switch (entry.entryType) {
    case 'Registered':
      return 'Entered in the register';
    case 'StatusChanged':
      return entry.fromStatus && entry.toStatus
        ? `${formatLabel(entry.fromStatus)} → ${formatLabel(entry.toStatus)}`
        : 'Status changed';
    case 'ConditionAssessed':
      return entry.fromCondition && entry.toCondition && entry.fromCondition !== entry.toCondition
        ? `Assessed ${formatLabel(entry.fromCondition).toLowerCase()} → ${formatLabel(entry.toCondition).toLowerCase()}`
        : `Assessed ${formatLabel(entry.toCondition ?? 'Unknown').toLowerCase()}`;
    case 'Maintenance':
      return 'Maintenance carried out';
  }
}
