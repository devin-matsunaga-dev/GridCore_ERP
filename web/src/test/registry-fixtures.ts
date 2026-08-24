import type { Asset, AssetHistoryEntry } from '@/api/assets';
import type { Customer, ServiceAccount, ServiceLocation } from '@/api/customers';
import type { StockItem, StockMovement, Warehouse } from '@/api/inventory';

/**
 * Rows shaped exactly as the host returns them, for the registry screen tests. Deliberately in the
 * seeded demo world — Rota, Saipan and Tinian — so a test reads like the app it is testing.
 */

export function customer(overrides: Partial<Customer> = {}): Customer {
  return {
    id: '0192f000-0000-7000-8000-000000000001',
    accountNumber: 'C-000001',
    name: 'Songsong Bakery',
    contactName: 'Maria Taimanao',
    email: 'maria@songsong-bakery.test',
    phone: '+1 670 555 0142',
    class: 'Commercial',
    status: 'Active',
    allowedTransitions: ['Suspended', 'Closed'],
    depositHeld: 450,
    registeredAt: '2026-02-11T00:30:00+00:00',
    statusChangedAt: '2026-03-01T00:30:00+00:00',
    statusReason: 'Service started',
    ...overrides,
  };
}

export function serviceLocation(overrides: Partial<ServiceLocation> = {}): ServiceLocation {
  return {
    id: '0192f000-0000-7000-8000-000000000101',
    locationCode: 'L-000001',
    address: {
      line1: '12 Songsong Village Road',
      line2: null,
      city: 'Songsong',
      region: 'Rota',
      country: 'MP',
      postalCode: '96951',
    },
    formattedAddress: '12 Songsong Village Road, Songsong, Rota, MP 96951',
    description: 'Bakery frontage',
    isActive: true,
    statusReason: null,
    registeredAt: '2026-02-11T00:30:00+00:00',
    ...overrides,
  };
}

export function serviceAccount(overrides: Partial<ServiceAccount> = {}): ServiceAccount {
  return {
    id: '0192f000-0000-7000-8000-000000000201',
    accountNumber: 'A-000001',
    customerId: customer().id,
    serviceLocationId: serviceLocation().id,
    status: 'Active',
    allowedTransitions: ['Disconnected', 'Closed'],
    openedAt: '2026-02-12T00:30:00+00:00',
    serviceStartedAt: '2026-02-14T00:30:00+00:00',
    serviceEndedAt: null,
    statusChangedAt: '2026-02-14T00:30:00+00:00',
    statusReason: 'Meter energised',
    history: [
      {
        id: '0192f000-0000-7000-8000-000000000301',
        fromStatus: null,
        toStatus: 'Pending',
        reason: 'Application received',
        actorId: 'demo:agent',
        actorName: 'Wes Store (demo)',
        recordedAt: '2026-02-12T00:30:00+00:00',
      },
      {
        id: '0192f000-0000-7000-8000-000000000302',
        fromStatus: 'Pending',
        toStatus: 'Active',
        reason: 'Meter energised',
        actorId: 'demo:agent',
        actorName: 'Wes Store (demo)',
        recordedAt: '2026-02-14T00:30:00+00:00',
      },
    ],
    ...overrides,
  };
}

export function asset(overrides: Partial<Asset> = {}): Asset {
  return {
    id: '0192f000-0000-7000-8000-000000000401',
    assetTag: 'AST-000001',
    class: 'Transformer',
    name: 'Songsong pole-top transformer',
    serialNumber: 'TX-88213',
    manufacturer: 'Hitachi',
    model: 'ZS-50',
    installedOn: '2019-06-04',
    status: 'InService',
    allowedTransitions: ['UnderMaintenance', 'InStorage', 'Retired'],
    condition: 'Good',
    latitude: 14.142_000,
    longitude: 145.185_000,
    locationNote: 'Pole 42, Songsong Village Road',
    registeredAt: '2026-02-11T00:30:00+00:00',
    statusChangedAt: '2026-02-20T00:30:00+00:00',
    statusReason: 'Energised',
    conditionAssessedAt: '2026-05-02T00:30:00+00:00',
    history: [],
    ...overrides,
  };
}

export function assetHistoryEntry(overrides: Partial<AssetHistoryEntry> = {}): AssetHistoryEntry {
  return {
    id: '0192f000-0000-7000-8000-000000000501',
    entryType: 'ConditionAssessed',
    fromStatus: null,
    toStatus: null,
    fromCondition: 'Excellent',
    toCondition: 'Good',
    note: 'Annual inspection, minor corrosion on the tank',
    workOrderId: null,
    actorId: 'demo:inspector',
    actorName: 'Wes Store (demo)',
    recordedAt: '2026-05-02T00:30:00+00:00',
    ...overrides,
  };
}

export function warehouse(overrides: Partial<Warehouse> = {}): Warehouse {
  return {
    id: '0192f000-0000-7000-8000-000000000601',
    code: 'ROTA',
    name: 'Rota Warehouse',
    location: 'Songsong',
    isActive: true,
    linesHeld: 7,
    linesBelowMinimum: 2,
    ...overrides,
  };
}

export function stockItem(overrides: Partial<StockItem> = {}): StockItem {
  return {
    id: '0192f000-0000-7000-8000-000000000701',
    itemCode: 'ITM-000001',
    name: 'LV service connector',
    category: 'Hardware',
    unit: 'Each',
    description: 'Insulation-piercing connector, 16–95 mm²',
    manufacturerPartNumber: 'IPC-95',
    unitCost: 4.5,
    isActive: true,
    statusReason: null,
    totalOnHand: 120,
    isBelowMinimum: false,
    registeredAt: '2026-02-11T00:30:00+00:00',
    levels: [
      {
        warehouseId: warehouse().id,
        quantityOnHand: 120,
        minimumQuantity: 40,
        isBelowMinimum: false,
        lastMovedAt: '2026-06-01T00:30:00+00:00',
      },
    ],
    movements: [],
    ...overrides,
  };
}

export function stockMovement(overrides: Partial<StockMovement> = {}): StockMovement {
  return {
    id: '0192f000-0000-7000-8000-000000000801',
    movementType: 'Receipt',
    warehouseId: warehouse().id,
    quantityChange: 100,
    quantityOnHandAfter: 120,
    unitCost: 4.5,
    value: 450,
    reference: 'PO-2026-014',
    workOrderId: null,
    note: null,
    actorId: 'demo:storeman',
    actorName: 'Wes Store (demo)',
    recordedAt: '2026-06-01T00:30:00+00:00',
    ...overrides,
  };
}
