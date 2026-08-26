import type { Asset, AssetHistoryEntry } from '@/api/assets';
import type { AccountCharge, FeeScheduleEntry } from '@/api/billing';
import type {
  AccountArrears,
  AccountStatement,
  AccountTransition,
  ApplicationDocument,
  ApplicationReference,
  ContactMethod,
  Customer,
  CustomerContact,
  CustomerNote,
  CustomerProfile,
  DepositEntry,
  DepositAccountRequirement,
  DepositLedger,
  DepositRequirement,
  Delinquency,
  DisconnectionEligibility,
  DunningNotice,
  DunningStep,
  ServiceAccount,
  ServiceApplication,
  ServiceLocation,
  StatementEntry,
} from '@/api/customers';
import type { StockItem, StockMovement, Warehouse } from '@/api/inventory';
import type { Meter } from '@/api/metering';

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
    statusEffectiveOn: '2026-03-01',

    // Still on the class they were registered under, which is the ordinary case and the one a
    // screen has to read as "since registration" rather than as "unknown".
    classChangedAt: null,
    classEffectiveOn: null,
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

/**
 * A contact as the register returns it, with its methods on it — the list endpoint includes them,
 * unlike `GET /api/service-accounts`, whose rows carry no history.
 */
export function customerContact(overrides: Partial<CustomerContact> = {}): CustomerContact {
  return {
    id: '0192f000-0000-7000-8000-000000000501',
    customerId: '0192f000-0000-7000-8000-000000000001',
    name: 'Rosa Taimanao',
    relationship: 'Spouse',
    isAuthorisedToDiscuss: true,
    methods: [contactMethod()],
    recordedAt: '2026-03-02T00:30:00+00:00',
    ...overrides,
  };
}

/**
 * One entry in a customer's note log (WP-2.13).
 *
 * Defaults to an ordinary inbound call about nothing in particular: unpinned, unlinked, no follow-up
 * and not a correction. Every interesting shape — pinned, linked, corrected — is an override, which
 * is what keeps a test's setup to the one fact it is about.
 */
export function customerNote(overrides: Partial<CustomerNote> = {}): CustomerNote {
  return {
    id: '0192f000-0000-7000-8000-000000000701',
    customerId: '0192f000-0000-7000-8000-000000000001',
    serviceAccountId: null,
    kind: 'InboundCall',
    isInteraction: true,
    body: 'Rang to ask when the meter would be read.',
    followUpOn: null,
    linkKind: null,
    linkedEntityId: null,
    linkedReference: null,
    correctsNoteId: null,
    isPinned: false,
    actorId: 'demo:customer-service',
    actorName: 'Ana Cruz (demo)',
    recordedAt: '2026-08-20T00:30:00+00:00',
    ...overrides,
  };
}

export function contactMethod(overrides: Partial<ContactMethod> = {}): ContactMethod {
  return {
    id: '0192f000-0000-7000-8000-000000000601',
    kind: 'Mobile',
    value: '+1 670 555 0188',
    isPrimary: true,
    recordedAt: '2026-03-02T00:30:00+00:00',
    ...overrides,
  };
}

/**
 * A profile as the host answers one. The mailing address is the RESOLVED address — the override
 * when there is one, the service address otherwise — which is why `source` rides beside it, and why
 * a fixture that only set `mailingAddress` would be testing a response the host never sends.
 */
export function customerProfile(overrides: Partial<CustomerProfile> = {}): CustomerProfile {
  const premise = serviceLocation();

  return {
    customerId: '0192f000-0000-7000-8000-000000000001',
    mailingAddress: premise.address,
    formattedMailingAddress: premise.formattedAddress,
    source: 'ServiceAddress',
    serviceAddress: premise.address,
    serviceLocationId: premise.id,
    billDeliveryChannel: 'Post',
    outageNotices: true,
    dunningNotices: true,
    preferredLanguage: 'English',
    updatedAt: null,
    ...overrides,
  };
}

/**
 * A meter as the register returns it. Fitted at a premise, never at an account — the meter carries
 * a `serviceLocationId` and no account of any kind, which is the whole shape of WP-2.1.
 */
/** One movement of a customer's security deposit, as the ledger endpoint returns it. */
export function depositEntry(overrides: Partial<DepositEntry> = {}): DepositEntry {
  return {
    id: '0192f000-0000-7000-8000-000000000701',
    customerId: customer().id,
    kind: 'Collected',
    amount: 450,
    signedAmount: 450,
    balanceAfter: 450,
    currency: 'USD',
    isInterestBearing: false,
    billId: null,
    billNumber: null,
    serviceAccountId: null,
    reason: 'Collected at intake.',
    actorId: 'demo:cashier',
    actorName: 'Rita Atalig (demo)',
    recordedAt: '2026-02-11T00:30:00+00:00',
    ...overrides,
  };
}

/**
 * A customer's deposit position. The default is the seeded commercial customer: the schedule is
 * covered exactly, which is the case a screen has to render as "met" rather than as "short by
 * nothing".
 */
export function depositLedger(overrides: Partial<DepositLedger> = {}): DepositLedger {
  return {
    customerId: customer().id,
    accountNumber: customer().accountNumber,
    balance: 450,
    currency: 'USD',
    requirement: depositRequirement(),
    isInterestBearing: false,
    entries: [depositEntry()],
    ...overrides,
  };
}

/**
 * What the schedule asks of one customer (WP-2.17): one commercial electricity account, covered.
 *
 * A composite since the schedule was re-keyed on (class × service) — a customer taking three
 * supplies is assessed three times, and one figure could only ever have described one of them.
 */
export function depositRequirement(overrides: Partial<DepositRequirement> = {}): DepositRequirement {
  return {
    customerId: customer().id,
    accountNumber: customer().accountNumber,
    customerClass: 'Commercial',
    currency: 'USD',
    heldAmount: 450,
    requiredAmount: 450,
    shortfallAmount: 0,
    isCovered: true,
    assessedAt: '2026-08-26T10:15:00+00:00',
    accounts: [depositAccountRequirement()],
    ...overrides,
  };
}

/** One open account's share of a deposit requirement. */
export function depositAccountRequirement(
  overrides: Partial<DepositAccountRequirement> = {},
): DepositAccountRequirement {
  return {
    serviceAccountId: serviceAccount().id,
    accountNumber: serviceAccount().accountNumber,
    serviceLocationId: serviceLocation().id,
    status: 'Active',
    serviceType: 'Electricity',
    isMetered: true,
    requiredAmount: 450,
    minimumAmount: 450,
    isUsageBased: false,
    averageMonthlyUsage: null,
    usageMonths: 2,
    usageRate: 0.32,
    hasUsageHistory: false,
    description: 'Commercial electricity: the greater of $450 and two months of average usage.',
    ruleId: '0192f000-0000-7000-8000-0000000007f1',
    ...overrides,
  };
}

/** One line of an account statement (WP-2.14). */
export function statementEntry(overrides: Partial<StatementEntry> = {}): StatementEntry {
  return {
    date: '2026-07-05',
    occurredAt: '2026-07-05T00:00:00+00:00',
    kind: 'BillIssued',
    description: 'Bill BIL-000001 for 1 Jun 2026 to 30 Jun 2026',
    reference: 'BIL-000001',
    amount: 120,
    depositAmount: 0,
    balanceAfter: 120,
    depositHeldAfter: 0,
    billId: '0192f000-0000-7000-8000-000000000a01',
    paymentId: null,
    depositEntryId: null,
    serviceAccountId: '0192f000-0000-7000-8000-000000000201',
    accountNumber: 'A-000001',
    ...overrides,
  };
}

/**
 * An account statement (WP-2.14).
 *
 * The default is a real one and it PROVES OUT: opening 0, one bill of 120, closing 120. A fixture
 * that did not add up would let a test pass against a document the screen is supposed to refuse.
 */
export function accountStatement(overrides: Partial<AccountStatement> = {}): AccountStatement {
  return {
    customerId: '0192f000-0000-7000-8000-000000000001',
    accountNumber: 'C-000001',
    customerName: 'Sablan Family Residence',
    mailingAddress: '12 Beach Road, Songsong, Rota',
    from: '2026-07-01',
    to: '2026-07-31',
    currency: 'USD',
    openingBalance: 0,
    closingBalance: 120,
    openingDepositHeld: 0,
    closingDepositHeld: 0,
    entries: [statementEntry()],
    billed: 120,
    corrected: 0,
    paid: 0,
    depositApplied: 0,
    isTruncated: false,
    producedAt: '2026-08-26T10:00:00+00:00',
    producedById: 'demo:customer-service',
    producedByName: 'Ana Cruz (demo)',
    ...overrides,
  };
}

export function meter(overrides: Partial<Meter> = {}): Meter {
  return {
    id: '0192f000-0000-7000-8000-000000000401',
    meterNumber: 'MTR-000001',
    serialNumber: 'SEN-4471102',
    type: 'SinglePhase',
    manufacturer: 'Sensus',
    model: 'iConA',
    registerDigits: 5,
    registerCapacity: 100000,
    status: 'Installed',
    isFitted: true,
    allowedTransitions: ['Faulty', 'Removed'],
    allowedStatusChanges: ['Faulty'],
    serviceLocationId: serviceLocation().id,
    serviceLocation: {
      id: serviceLocation().id,
      locationCode: serviceLocation().locationCode,
      formattedAddress: serviceLocation().formattedAddress,
      isActive: true,
    },
    installedAt: '2026-02-14T00:30:00+00:00',
    installationReading: 14820.5,
    registeredAt: '2026-02-10T00:30:00+00:00',
    statusChangedAt: '2026-02-14T00:30:00+00:00',
    statusReason: 'New connection, meter set on the north wall',
    history: [],
    ...overrides,
  };
}

/**
 * One row of the transition register (WP-2.15).
 *
 * Defaults to a class change, which is the kind with both sides filled in — the move kinds are the
 * ones with a null on one side, so a test that wants one says so and gets a row that reads correctly.
 */
export function accountTransition(overrides: Partial<AccountTransition> = {}): AccountTransition {
  return {
    id: '0192f000-0000-7000-8000-000000000401',
    customerId: customer().id,
    kind: 'ClassChanged',
    reasonCode: 'PremiseNowTrading',
    notes: 'Bakery opened in the front room.',
    effectiveOn: '2026-09-01',
    fromValue: 'Residential',
    toValue: 'Commercial',
    fromServiceAccountId: null,
    toServiceAccountId: null,
    depositCarried: 0,
    currency: null,
    depositEntryId: null,
    actorId: 'demo:agent',
    actorName: 'Ana Cruz (demo)',
    recordedAt: '2026-08-26T10:15:00+00:00',
    ...overrides,
  };
}

export function serviceAccount(overrides: Partial<ServiceAccount> = {}): ServiceAccount {
  return {
    id: '0192f000-0000-7000-8000-000000000201',
    accountNumber: 'A-000001',
    customerId: customer().id,
    serviceLocationId: serviceLocation().id,
    serviceType: 'Electricity',
    isMetered: true,
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

export function applicationDocument(overrides: Partial<ApplicationDocument> = {}): ApplicationDocument {
  return {
    id: '0192f000-0000-7000-8000-000000000501',
    kind: 'PhotoId',
    fileName: 'photo-id.pdf',
    contentType: 'application/pdf',
    sizeInBytes: 20_480,
    checksum: 'a'.repeat(64),
    uploadedAt: '2026-08-27T09:40:00+00:00',
    actorId: 'demo:agent',
    actorName: 'Ana Cruz (demo)',
    ...overrides,
  };
}

/**
 * An application as the host returns it — submitted, nothing attached, so the checklist reads as
 * two outstanding lines. Every test that wants evidence says so by passing `documents` and
 * `checklist` together, which is what the host does.
 */
export function serviceApplication(overrides: Partial<ServiceApplication> = {}): ServiceApplication {
  return {
    id: '0192f000-0000-7000-8000-000000000601',
    applicationNumber: 'AP-000001',
    customerId: customer().id,
    serviceLocationId: serviceLocation().id,
    serviceType: 'Electricity',
    type: 'ResidentialConnection',
    status: 'Submitted',
    allowedTransitions: ['UnderReview', 'Withdrawn'],
    isOpen: true,
    requestedOn: '2026-09-03',
    notes: 'Filed at the counter.',
    checklist: [
      { kind: 'PhotoId', isSatisfied: false, documentId: null, uploadedAt: null },
      { kind: 'ProofOfOccupancy', isSatisfied: false, documentId: null, uploadedAt: null },
    ],
    missingDocuments: ['PhotoId', 'ProofOfOccupancy'],
    isDocumentationComplete: false,
    documents: [],
    submittedAt: '2026-08-27T09:30:00+00:00',
    submittedById: 'demo:agent',
    submittedByName: 'Ana Cruz (demo)',
    reviewStartedAt: null,
    reviewerId: null,
    reviewerName: null,
    decidedAt: null,
    decidedById: null,
    decidedByName: null,
    decisionReasonCode: null,
    decisionNotes: null,
    serviceAccountId: null,
    replacesApplicationId: null,
    ...overrides,
  };
}

/** The host's own checklist, decision lists and upload policy, as `/api/service-application-reference` returns them. */
export function applicationReference(overrides: Partial<ApplicationReference> = {}): ApplicationReference {
  return {
    types: [
      { type: 'ResidentialConnection', requiredDocuments: ['PhotoId', 'ProofOfOccupancy'] },
      { type: 'CommercialConnection', requiredDocuments: ['PhotoId', 'ProofOfOccupancy', 'BusinessLicence'] },
    ],
    documentKinds: ['PhotoId', 'ProofOfOccupancy', 'BusinessLicence', 'Other'],
    allowedContentTypes: ['application/pdf', 'image/jpeg', 'image/png'],
    maxSizeInBytes: 10 * 1024 * 1024,
    reasonCodes: {
      Approved: ['DocumentsVerified', 'ApprovedByException', 'Other'],
      Rejected: [
        'DocumentsIncomplete',
        'IdentityNotVerified',
        'OccupancyNotProven',
        'PremiseNotServiceable',
        'OutstandingBalance',
        'DuplicateApplication',
        'Other',
      ],
      Withdrawn: ['ApplicantWithdrew', 'ApplicantUnreachable', 'SupersededByAnotherApplication', 'Other'],
    },
    reasonCodesRequiringNotes: ['Other', 'ApprovedByException'],
    ...overrides,
  };
}

/** One published fee, priced for the day the catalogue was asked about (WP-2.16). */
export function feeScheduleEntry(overrides: Partial<FeeScheduleEntry> = {}): FeeScheduleEntry {
  return {
    code: 'ServiceConnection',
    name: 'Service connection',
    description: 'Establishing supply at a premise. Demo figure.',
    serviceType: 'Electricity',
    // Flat by default (WP-2.19). The one rate fee GridCore publishes is the late charge, which is
    // never offered at the counter — a test that wants one asks for it by name.
    basis: 'Flat',
    amount: 135,
    rate: null,
    currency: 'USD',
    effectiveFrom: '2026-01-01',
    feeScheduleId: '0192f000-0000-7000-8000-000000000701',
    ...overrides,
  };
}

/** One fee raised against a service account (WP-2.16). */
export function accountCharge(overrides: Partial<AccountCharge> = {}): AccountCharge {
  return {
    id: '0192f000-0000-7000-8000-000000000801',
    serviceAccountId: serviceAccount().id,
    accountNumber: 'A-000001',
    customerId: customer().id,
    customerName: customer().name,
    code: 'ServiceConnection',
    description: 'Service connection',
    amount: 135,
    currency: 'USD',
    basis: 'Flat',
    rate: null,
    basisAmount: null,
    feeScheduleId: feeScheduleEntry().feeScheduleId,
    scheduleEffectiveFrom: '2026-01-01',
    raisedOn: '2026-08-27',
    reason: 'New connection approved.',
    status: 'Pending',
    allowedTransitions: ['Billed', 'Cancelled'],
    isPending: true,
    billId: null,
    billNumber: null,
    raisedAt: '2026-08-27T10:00:00+00:00',
    statusChangedAt: '2026-08-27T10:00:00+00:00',
    statusReason: null,
    actorId: 'demo:agent',
    actorName: 'Ana Cruz (demo)',
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

/**
 * One account's delinquency picture (WP-2.19): $200 past due and ninety days late, a $50 threshold,
 * no notice served and no deposit held — the state a screen has the most to say about.
 */
export function delinquency(overrides: Partial<Delinquency> = {}): Delinquency {
  const account = serviceAccount();

  return {
    serviceAccountId: account.id,
    accountNumber: account.accountNumber,
    customerId: customer().id,
    customerName: customer().name,
    accountStatus: 'Active',
    arrears: accountArrears(),
    depositHeld: 0,
    steps: dunningSteps(),
    dueStep: dunningSteps()[2],
    notices: [],
    eligibility: disconnectionEligibility(),
    ...overrides,
  };
}

/** What an account owes, aged. Past due and not-yet-due deliberately differ — the package's hinge. */
export function accountArrears(overrides: Partial<AccountArrears> = {}): AccountArrears {
  return {
    currency: 'USD',
    asOf: '2026-09-01',
    outstandingAmount: 280,
    pastDueAmount: 200,
    currentAmount: 80,
    oldestDueDate: '2026-06-03',
    daysPastDue: 90,
    isInArrears: true,
    buckets: [
      { label: 'Not yet due', fromDays: 0, toDays: 0, amount: 80 },
      { label: '1-30 days', fromDays: 1, toDays: 30, amount: 0 },
      { label: '31-60 days', fromDays: 31, toDays: 60, amount: 0 },
      { label: '61-90 days', fromDays: 61, toDays: 90, amount: 200 },
      { label: 'Over 90 days', fromDays: 91, toDays: null, amount: 0 },
    ],
    bills: [
      {
        id: '0192f000-0000-7000-8000-000000000901',
        billNumber: 'BIL-000001',
        dueDate: '2026-06-03',
        balance: 200,
        daysPastDue: 90,
        isPastDue: true,
      },
    ],
    ...overrides,
  };
}

/** The shipped dunning sequence, as the host publishes it. */
export function dunningSteps(): DunningStep[] {
  return [
    {
      noticeType: 'Reminder',
      sequence: 1,
      daysPastDue: 10,
      minimumArrears: 10,
      waitingPeriodDays: 0,
      currency: 'USD',
      name: 'Payment reminder',
      message: 'Your account is past due. Demo wording; not an authoritative notice.',
    },
    {
      noticeType: 'Delinquency',
      sequence: 2,
      daysPastDue: 30,
      minimumArrears: 25,
      waitingPeriodDays: 0,
      currency: 'USD',
      name: 'Notice of delinquency',
      message: 'Your account is delinquent. Demo wording; not an authoritative notice.',
    },
    {
      noticeType: 'Disconnection',
      sequence: 3,
      daysPastDue: 45,
      minimumArrears: 50,
      waitingPeriodDays: 10,
      currency: 'USD',
      name: 'Notice of disconnection',
      message: 'Service is scheduled for disconnection. Demo wording; not an authoritative notice.',
    },
  ];
}

/** One notice served, with the day it went out — the record that makes a disconnection defensible. */
export function dunningNotice(overrides: Partial<DunningNotice> = {}): DunningNotice {
  const account = serviceAccount();

  return {
    id: '0192f000-0000-7000-8000-000000000911',
    serviceAccountId: account.id,
    accountNumber: account.accountNumber,
    customerId: customer().id,
    customerName: customer().name,
    noticeType: 'Disconnection',
    servedOn: '2026-08-10',
    arrearsAmount: 200,
    currency: 'USD',
    daysPastDue: 68,
    waitingPeriodDays: 10,
    effectiveFrom: '2026-08-20',
    notes: null,
    actorId: 'auth0|cs-agent',
    actorName: 'Ana Cruz',
    recordedAt: '2026-08-10T09:00:00+00:00',
    ...overrides,
  };
}

/**
 * Where an account stands against the four tests. Not eligible by default and blocked on the notice,
 * because that is the state a screen has to explain rather than the one it celebrates.
 */
export function disconnectionEligibility(
  overrides: Partial<DisconnectionEligibility> = {},
): DisconnectionEligibility {
  return {
    serviceAccountId: serviceAccount().id,
    asOf: '2026-09-01',
    currency: 'USD',
    arrearsBeforeOffset: 200,
    depositHeldBeforeOffset: 0,
    offsetAmount: 0,
    arrearsAfterOffset: 200,
    depositHeldAfterOffset: 0,
    threshold: 50,
    disconnectionNoticeServedOn: null,
    waitingPeriodDays: 10,
    eligibleFrom: null,
    arrangementStatus: null,
    isEligible: false,
    depositClearsArrears: false,
    isOffsetApplied: false,
    tests: [
      { name: 'Arrears at or over the published threshold', isSatisfied: true, detail: '200.00 past due against a threshold of 50.00.' },
      { name: 'Disconnection notice served', isSatisfied: false, detail: 'No disconnection notice has been served on this account.' },
      { name: 'Statutory waiting period elapsed', isSatisfied: false, detail: 'Nothing has started the 10-day period.' },
      { name: 'No payment arrangement in force', isSatisfied: true, detail: 'No payment arrangement is recorded against this account.' },
    ],
    blockers: ['Disconnection notice served', 'Statutory waiting period elapsed'],
    ...overrides,
  };
}
