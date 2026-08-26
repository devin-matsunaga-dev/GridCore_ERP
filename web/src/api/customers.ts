import { useQueries, useQuery } from '@tanstack/react-query';
import { env } from '@/lib/env';
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

/**
 * Mirrors `GridCore.Contracts.Services.ServiceType` (WP-2.17).
 *
 * In `Contracts` on the host and shared by three modules, so this list is the one vocabulary the
 * deposit schedule, the tariff catalogue and the meter guard all key on. The order is the enum's.
 */
export const serviceTypes = ['Electricity', 'Water', 'Gas', 'Wastewater'] as const;
export type ServiceType = (typeof serviceTypes)[number];

/**
 * Whether a device at the premise measures what an account of this service consumes.
 *
 * Duplicated from `ServiceTypes.IsMetered` deliberately, and kept to one line: the host is the
 * authority and every response that matters carries an `isMetered` flag of its own, but a screen
 * choosing what to offer before a request has been made needs the answer locally.
 */
export function isMeteredService(serviceType: ServiceType): boolean {
  return serviceType !== 'Wastewater';
}

/** What each service reads as on a screen. Sentence case, as DESIGN.md asks. */
const serviceTypeLabels: Record<ServiceType, string> = {
  Electricity: 'Electricity',
  Water: 'Water',
  Gas: 'Gas',
  Wastewater: 'Wastewater',
};

/** What a service reads as. */
export function serviceTypeLabel(serviceType: ServiceType): string {
  return serviceTypeLabels[serviceType];
}

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
  /** The day the current status applies from (WP-2.15); null means "since registration". */
  statusEffectiveOn: string | null;
  classChangedAt: string | null;
  /**
   * The day the current class applies from (WP-2.15); null means "still on the registered class".
   *
   * **Not `classChangedAt`.** That is when a rep typed it; this is when the utility says it happened,
   * and it is what the billing pass prices from. A back-dated re-classification is the case that
   * makes the two disagree, which is why both are on the record.
   */
  classEffectiveOn: string | null;
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
  /** Which supply this account takes (WP-2.17). Fixed at opening. */
  serviceType: ServiceType;
  /** Whether a device at the premise measures it — derived from `serviceType` by the host. */
  isMetered: boolean;
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
  serviceType?: ServiceType | '';
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
  serviceType: ServiceType;
  reason?: string | null;
};

/**
 * Mirrors `DepositRuleResponse` — the schedule, reference data on the host.
 *
 * Keyed on (class × service) since WP-2.17: `amount` is the published floor for the pair, with no
 * usage in it, because a screen listing the schedule is showing what a rule says rather than what
 * one customer would pay. What a particular customer is asked for is `DepositRequirement`.
 */
export type DepositRule = {
  customerClass: CustomerClass;
  serviceType: ServiceType;
  isMetered: boolean;
  amount: number;
  minimumAmount: number;
  /** Months of average usage the rule takes above the floor; `null` on a flat deposit. */
  usageMonths: number | null;
  /** What one unit is priced at for deposit purposes; `null` on a flat deposit. */
  usageRate: number | null;
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
  serviceType: ServiceType;
  depositCollected?: number;
  startService?: boolean;
  reason?: string | null;
};

/** Mirrors `DepositOutcomeResponse`. */
export type DepositOutcome = {
  customerClass: CustomerClass;
  serviceType: ServiceType;
  assessedAmount: number;
  collectedAmount: number;
  ruleId: string;
};

/** Mirrors `ContactMethodKind`. Phone and mobile are two kinds, not one with a flag. */
export const contactMethodKinds = ['Phone', 'Mobile', 'Email'] as const;
export type ContactMethodKind = (typeof contactMethodKinds)[number];

/** Mirrors `ContactMethodResponse`. */
export type ContactMethod = {
  id: string;
  kind: ContactMethodKind;
  value: string;
  /** The one to try first for its kind. Exactly one per kind the contact has. */
  isPrimary: boolean;
  recordedAt: string;
};

/** Mirrors `ContactResponse`. */
export type CustomerContact = {
  id: string;
  customerId: string;
  name: string;
  relationship: string | null;
  /**
   * Whether a rep may discuss the account with them. Moving this needs `customers.authorise` on
   * the host — a narrower grant than the `customers.write` that opened the screen.
   */
  isAuthorisedToDiscuss: boolean;
  methods: ContactMethod[];
  recordedAt: string;
};

/** Mirrors `BillDeliveryChannel`. */
export const billDeliveryChannels = ['Post', 'Email', 'Both'] as const;
export type BillDeliveryChannel = (typeof billDeliveryChannels)[number];

/** Mirrors `CommunicationLanguage`. */
export const communicationLanguages = ['English', 'Chamorro', 'Carolinian'] as const;
export type CommunicationLanguage = (typeof communicationLanguages)[number];

/** Mirrors `MailingAddressSource` — where the address on screen came from. */
export const mailingAddressSources = ['None', 'ServiceAddress', 'Override'] as const;
export type MailingAddressSource = (typeof mailingAddressSources)[number];

/**
 * Mirrors `CustomerProfileResponse`.
 *
 * `mailingAddress` is **resolved**: it is the override when there is one and the service address
 * otherwise, which is why `source` rides beside it. `serviceAddress` is the default reported
 * separately, so a screen can say what clearing the override would fall back to without guessing.
 */
export type CustomerProfile = {
  customerId: string;
  mailingAddress: Address | null;
  formattedMailingAddress: string | null;
  source: MailingAddressSource;
  serviceAddress: Address | null;
  serviceLocationId: string | null;
  billDeliveryChannel: BillDeliveryChannel;
  outageNotices: boolean;
  dunningNotices: boolean;
  preferredLanguage: CommunicationLanguage;
  /** Null while these are still the defaults — nobody has saved a profile for this customer. */
  updatedAt: string | null;
};

/** Mirrors `ContactMethodRequest`. */
export type ContactMethodInput = {
  kind: ContactMethodKind;
  value: string;
  isPrimary?: boolean;
};

/** Mirrors `CreateContactRequest`. */
export type CreateContactInput = {
  name: string;
  relationship?: string | null;
  isAuthorisedToDiscuss?: boolean;
  methods?: ContactMethodInput[];
};

/** Mirrors `UpdateContactRequest`. */
export type UpdateContactInput = {
  name: string;
  relationship?: string | null;
  isAuthorisedToDiscuss: boolean;
};

/**
 * Mirrors `UpdateCustomerProfileRequest`. The whole profile, never a patch: an omitted mailing
 * address and a cleared one have to stay tellable apart, and that is the distinction this carries.
 */
export type UpdateCustomerProfileInput = {
  billDeliveryChannel: BillDeliveryChannel;
  outageNotices: boolean;
  dunningNotices: boolean;
  preferredLanguage: CommunicationLanguage;
  mailingAddress?: AddressInput | null;
};

/** Mirrors `DepositEntryKind`. The order is the lifecycle's; the kind carries the direction. */
export const depositEntryKinds = ['Collected', 'Applied', 'Refunded', 'Transferred'] as const;
export type DepositEntryKind = (typeof depositEntryKinds)[number];

/** Mirrors `DepositEntryResponse` — one movement of a customer's security deposit. */
export type DepositEntry = {
  id: string;
  customerId: string;
  kind: DepositEntryKind;
  /** Always positive. The kind carries the direction, never the sign. */
  amount: number;
  /** The magnitude with its direction applied: `+` for money taken, `-` for money out. */
  signedAmount: number;
  /** What the utility held once this entry was applied. Stored, not recomputed. */
  balanceAfter: number;
  currency: string;
  isInterestBearing: boolean;
  billId: string | null;
  billNumber: string | null;
  serviceAccountId: string | null;
  reason: string | null;
  actorId: string;
  actorName: string | null;
  recordedAt: string;
};

/**
 * Mirrors `DepositLedgerResponse`.
 *
 * `balance` is `Customer.depositHeld` — the projection these entries add up to — and `assessedAmount`
 * is what the class schedule asks, so a screen can say whether the customer is short without a
 * second request. `shortfallAmount` is floored at zero by the host.
 */
export type DepositLedger = {
  customerId: string;
  accountNumber: string;
  balance: number;
  currency: string;
  requirement: DepositRequirement;
  isInterestBearing: boolean;
  entries: DepositEntry[];
};

/** Mirrors `DepositAccountRequirementResponse` — one open account's share of the deposit (WP-2.17). */
export type DepositAccountRequirement = {
  serviceAccountId: string;
  accountNumber: string;
  serviceLocationId: string;
  status: ServiceAccountStatus;
  serviceType: ServiceType;
  isMetered: boolean;
  requiredAmount: number;
  minimumAmount: number;
  /** Whether measured usage decided the figure rather than the published floor. */
  isUsageBased: boolean;
  averageMonthlyUsage: number | null;
  usageMonths: number | null;
  usageRate: number | null;
  /** Whether anything has actually been read at the premise. Different from `isUsageBased`. */
  hasUsageHistory: boolean;
  description: string;
  ruleId: string;
};

/**
 * Mirrors `DepositRequirementResponse` — what a customer holds against what the schedule now asks.
 *
 * A composite since WP-2.17, because a customer may take three supplies and be assessed for each:
 * `requiredAmount` is the sum over `accounts`, and `shortfallAmount` is floored at zero by the host.
 */
export type DepositRequirement = {
  customerId: string;
  accountNumber: string;
  customerClass: CustomerClass;
  currency: string;
  heldAmount: number;
  requiredAmount: number;
  shortfallAmount: number;
  isCovered: boolean;
  assessedAt: string;
  accounts: DepositAccountRequirement[];
};

/** Mirrors `CollectDepositRequest`. */
export type CollectDepositInput = {
  amount: number;
  isInterestBearing?: boolean;
  reason?: string | null;
};

/** Mirrors `ApplyDepositRequest`. */
export type ApplyDepositInput = {
  billId: string;
  amount: number;
  reason?: string | null;
};

/** Mirrors `RefundDepositRequest`. */
export type RefundDepositInput = {
  amount: number;
  reason?: string | null;
};

/** Mirrors `CustomerNoteKind`. The order is the enum's; `Note` is the one that is not a contact. */
export const noteKinds = [
  'Note',
  'InboundCall',
  'OutboundCall',
  'CounterVisit',
  'FieldVisit',
  'Complaint',
  'BillingDispute',
] as const;
export type CustomerNoteKind = (typeof noteKinds)[number];

/**
 * Mirrors `CustomerNoteLinkKind`.
 *
 * `WorkOrder` is stored but **not verified** by the host until WP-3.1 builds that register — a
 * work-order link therefore comes back with a `linkedReference` of `null`, because there is nobody
 * to ask what the number is. Bills and payments arrive with theirs.
 */
export const noteLinkKinds = ['Bill', 'Payment', 'WorkOrder'] as const;
export type CustomerNoteLinkKind = (typeof noteLinkKinds)[number];

/** Mirrors `CustomerNoteResponse` — one entry in a customer's note log. */
export type CustomerNote = {
  id: string;
  customerId: string;
  /** The account it is about, or `null` when it is about the person rather than one of their supplies. */
  serviceAccountId: string | null;
  kind: CustomerNoteKind;
  /** Whether it records a contact that took place rather than something a rep wrote down. */
  isInteraction: boolean;
  body: string;
  /** A day, never an instant: "ring them back on Thursday" is a day's work. */
  followUpOn: string | null;
  linkKind: CustomerNoteLinkKind | null;
  linkedEntityId: string | null;
  /** The bill or payment number, as printed. `null` on a work-order link — see `noteLinkKinds`. */
  linkedReference: string | null;
  /** The note this one corrects. The corrected note carries no pointer back — see `notes.ts`. */
  correctsNoteId: string | null;
  isPinned: boolean;
  actorId: string;
  actorName: string | null;
  recordedAt: string;
};

/** Mirrors `NoteLinkRequest`. */
export type NoteLinkInput = {
  kind: CustomerNoteLinkKind;
  entityId: string;
};

/** Mirrors `LogNoteRequest`. */
export type LogNoteInput = {
  kind: CustomerNoteKind;
  body: string;
  serviceAccountId?: string | null;
  followUpOn?: string | null;
  link?: NoteLinkInput | null;
};

/**
 * Mirrors `CorrectNoteRequest`.
 *
 * No customer and no account: a correction is filed where the note it corrects was filed, so those
 * come from the host rather than from here.
 */
export type CorrectNoteInput = {
  kind: CustomerNoteKind;
  body: string;
  followUpOn?: string | null;
  link?: NoteLinkInput | null;
};

/** How the note log is narrowed. Every field is optional; the host clamps the limit. */
export type CustomerNoteFilters = {
  kind?: CustomerNoteKind;
  serviceAccountId?: string;
  pinnedOnly?: boolean;
  limit?: number;
};

/** Mirrors `StatementEntryKind`. The order is the order lines of one day sort in. */
export const statementEntryKinds = [
  'BillIssued',
  'BillCorrected',
  'BillWithdrawn',
  'PaymentReceived',
  'DepositApplied',
  'DepositCollected',
  'DepositRefunded',
  'DepositTransferred',
] as const;
export type StatementEntryKind = (typeof statementEntryKinds)[number];

/**
 * Mirrors `StatementEntryResponse` — one line of an account statement.
 *
 * **Two signed columns, because a statement tracks two balances.** `amount` is the effect on what
 * the customer owes; `depositAmount` is the effect on what the utility holds for them. A deposit
 * collection moves only the second — it is a liability taken on, not a payment — and an application
 * moves both.
 */
export type StatementEntry = {
  date: string;
  occurredAt: string;
  kind: StatementEntryKind;
  description: string;
  reference: string | null;
  amount: number;
  depositAmount: number;
  balanceAfter: number;
  depositHeldAfter: number;
  /** What a reprint link is built from, on the lines that concern a bill. */
  billId: string | null;
  paymentId: string | null;
  depositEntryId: string | null;
  serviceAccountId: string | null;
  accountNumber: string | null;
};

/**
 * Mirrors `AccountStatementResponse` — a statement over a date range (WP-2.14).
 *
 * Composed by the host across bills, payments and the deposit ledger, and it proves out:
 * `openingBalance` plus every entry's `amount` is `closingBalance`. Nothing is stored — the host
 * builds it from records that already exist, which is why there is no id here to fetch it back by.
 */
export type AccountStatement = {
  customerId: string;
  accountNumber: string;
  customerName: string;
  mailingAddress: string | null;
  from: string;
  to: string;
  currency: string;
  openingBalance: number;
  closingBalance: number;
  openingDepositHeld: number;
  closingDepositHeld: number;
  entries: StatementEntry[];
  billed: number;
  corrected: number;
  paid: number;
  depositApplied: number;
  /** A register's history did not fit, so the opening balance may be short. Say so on screen. */
  isTruncated: boolean;
  producedAt: string;
  producedById: string;
  producedByName: string | null;
};

/** The range a statement is asked for. Both days are included; the host defaults to the last quarter. */
export type StatementRange = { from: string; to: string };

/**
 * Mirrors `AccountTransitionKind` (WP-2.15) — the two changes that alter what a customer is billed.
 *
 * The first two move the customer record; the last three move service between premises, and
 * `Transferred` is the pair of them done as one act.
 */
export const accountTransitionKinds = [
  'ClassChanged',
  'StatusChanged',
  'MovedIn',
  'MovedOut',
  'Transferred',
] as const;
export type AccountTransitionKind = (typeof accountTransitionKinds)[number];

/**
 * Mirrors `TransitionReasonCode` — the fixed list a transition must be recorded under.
 *
 * One list rather than one per kind, with `transitionReasonsFor` in `transitions.ts` saying which
 * codes fit which. `Other` is the escape hatch and is the one code that must carry free text; the
 * host refuses it silent, and the form asks for it before the host has to.
 */
export const transitionReasonCodes = [
  'Other',
  'CustomerRequest',
  'PremiseNowTrading',
  'PremiseNowResidential',
  'MisclassifiedAtIntake',
  'UnpaidBalance',
  'BalanceSettled',
  'IdentityDisputed',
  'Deceased',
  'NewOccupancy',
  'EndOfTenancy',
  'PropertyVacated',
  'PropertyDemolished',
  'Relocation',
] as const;
export type TransitionReasonCode = (typeof transitionReasonCodes)[number];

/**
 * Mirrors `AccountTransitionResponse` — one recorded transition.
 *
 * **`effectiveOn` is not `recordedAt`, and the difference is the point.** The second is when a rep
 * typed it; the first is when the utility says it happened, and it is what the billing pass will
 * price from. A screen that showed only one of them would hide a back-dated re-classification.
 *
 * `fromValue` / `toValue` read according to `kind`: a class name, a status name, or the number of
 * the account released or opened. A move-in has no before and a move-out has no after.
 */
export type AccountTransition = {
  id: string;
  customerId: string;
  kind: AccountTransitionKind;
  reasonCode: TransitionReasonCode;
  notes: string | null;
  effectiveOn: string;
  fromValue: string | null;
  toValue: string | null;
  fromServiceAccountId: string | null;
  toServiceAccountId: string | null;
  /** How much held deposit rode along. Zero on everything but a transfer — it moved nowhere. */
  depositCarried: number;
  currency: string | null;
  depositEntryId: string | null;
  actorId: string;
  actorName: string | null;
  recordedAt: string;
};

/** What the transition register is narrowed by. */
export type AccountTransitionFilters = {
  kind?: AccountTransitionKind;
  /** Matches an account on EITHER side — released or taken up. */
  serviceAccountId?: string;
  limit?: number;
};

/** The fields every transition write carries. */
export type TransitionInput = {
  reasonCode: TransitionReasonCode;
  /** Omitted means "today on the host". A rep dating it themselves is the back- and forward-dated case. */
  effectiveOn?: string;
  notes?: string;
};

export type ChangeCustomerClassInput = TransitionInput & { class: CustomerClass };
export type ChangeCustomerStatusInput = TransitionInput & { status: CustomerStatus };
export type MoveInInput = TransitionInput & { serviceLocationId: string };
export type MoveOutInput = TransitionInput & { serviceAccountId: string };
export type TransferServiceInput = TransitionInput & {
  fromServiceAccountId: string;
  toServiceLocationId: string;
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

  contacts: (customerId: string, signal?: AbortSignal) =>
    api.get<CustomerContact[]>(`/api/customers/${customerId}/contacts`, { signal }),

  profile: (customerId: string, signal?: AbortSignal) =>
    api.get<CustomerProfile>(`/api/customers/${customerId}/profile`, { signal }),

  addContact: (customerId: string, input: CreateContactInput) =>
    api.post<CustomerContact>(`/api/customers/${customerId}/contacts`, { json: input }),

  updateContact: (contactId: string, input: UpdateContactInput) =>
    api.put<CustomerContact>(`/api/customer-contacts/${contactId}`, { json: input }),

  removeContact: (contactId: string) => api.delete<void>(`/api/customer-contacts/${contactId}`),

  addContactMethod: (contactId: string, input: ContactMethodInput) =>
    api.post<CustomerContact>(`/api/customer-contacts/${contactId}/methods`, { json: input }),

  correctContactMethod: (contactId: string, methodId: string, value: string) =>
    api.put<CustomerContact>(`/api/customer-contacts/${contactId}/methods/${methodId}`, { json: { value } }),

  /**
   * Promotes a method. A POST sub-resource rather than a PUT of a boolean, because it changes a row
   * the caller did not name: whichever method held the primary place is demoted in the same act.
   */
  makeContactMethodPrimary: (contactId: string, methodId: string) =>
    api.post<CustomerContact>(`/api/customer-contacts/${contactId}/methods/${methodId}/primary`, {}),

  removeContactMethod: (contactId: string, methodId: string) =>
    api.delete<CustomerContact>(`/api/customer-contacts/${contactId}/methods/${methodId}`),

  saveProfile: (customerId: string, input: UpdateCustomerProfileInput) =>
    api.put<CustomerProfile>(`/api/customers/${customerId}/profile`, { json: input }),

  /**
   * One customer's note log (WP-2.13), pinned first and newest first within each group.
   *
   * `limit` is left to the host's default when the caller does not say — it clamps whatever arrives,
   * so a screen never has to guess a ceiling.
   */
  notes: (customerId: string, filters: CustomerNoteFilters = {}, signal?: AbortSignal) =>
    api.get<CustomerNote[]>(`/api/customers/${customerId}/notes`, {
      query: params({
        kind: filters.kind,
        serviceAccountId: filters.serviceAccountId,
        pinnedOnly: filters.pinnedOnly,
        limit: filters.limit === undefined ? undefined : String(filters.limit),
      }),
      signal,
    }),

  logNote: (customerId: string, input: LogNoteInput) =>
    api.post<CustomerNote>(`/api/customers/${customerId}/notes`, { json: input }),

  /**
   * Corrects a note by writing a new one that references it.
   *
   * A POST sub-resource rather than a PUT of the note, because the note is never edited — the host
   * answers a PUT with a 409 saying exactly that. The correction is a new row and comes back as one.
   */
  correctNote: (noteId: string, input: CorrectNoteInput) =>
    api.post<CustomerNote>(`/api/customer-notes/${noteId}/corrections`, { json: input }),

  /**
   * Pins a note or takes it back down. A PUT, because it sets one field to a value the caller states
   * and is idempotent — pinning a pinned note is not a conflict on the host either.
   */
  pinNote: (noteId: string, isPinned: boolean) =>
    api.put<CustomerNote>(`/api/customer-notes/${noteId}/pin`, { json: { isPinned } }),

  /** One customer's deposit: the balance, the schedule it is measured against, and every movement. */
  deposits: (customerId: string, signal?: AbortSignal) =>
    api.get<DepositLedger>(`/api/customers/${customerId}/deposits`, { signal }),

  /**
   * What the schedule asks of one customer today, across every open account they hold (WP-2.17).
   *
   * Its own resource rather than only riding on the ledger, because the two are asked at different
   * moments: the ledger is "what is this customer's deposit", and this is "what would we ask them
   * for now" — the question a class change or a new supply prompts. Gated on `customers.read` on the
   * host: quoting a shortfall is clerical work, and `customers.deposit` stays for taking the money.
   */
  depositAssessment: (customerId: string, signal?: AbortSignal) =>
    api.get<DepositRequirement>(`/api/customers/${customerId}/deposits/assessment`, { signal }),

  /**
   * The three deposit movements (WP-2.12), each a POST sub-resource rather than a PUT of a balance:
   * the balance is a projection of immutable entries and is not a field anybody sets. All three
   * need `customers.deposit` on the host — narrower than the `customers.write` that opened the page.
   */
  collectDeposit: (customerId: string, input: CollectDepositInput) =>
    api.post<DepositEntry>(`/api/customers/${customerId}/deposits/collections`, { json: input }),

  applyDeposit: (customerId: string, input: ApplyDepositInput) =>
    api.post<DepositEntry>(`/api/customers/${customerId}/deposits/applications`, { json: input }),

  refundDeposit: (customerId: string, input: RefundDepositInput) =>
    api.post<DepositEntry>(`/api/customers/${customerId}/deposits/refunds`, { json: input }),

  /**
   * An account statement over a range (WP-2.14).
   *
   * A GET the host AUDITS, so it is fetched when a rep asks for a statement and not as part of
   * loading the page — the call every other query on the 360 makes in the opposite direction. Needs
   * `customers.documents`, which is narrower than the `customers.read` that opened the page.
   */
  statement: (customerId: string, range: StatementRange, signal?: AbortSignal) =>
    api.get<AccountStatement>(`/api/customers/${customerId}/documents/statement`, {
      query: { from: range.from, to: range.to },
      signal,
    }),

  /**
   * The payment history as a CSV file.
   *
   * Comes back as text rather than JSON — it is a file, and `Content-Disposition` is what names it.
   * The name is not on this answer, so the caller builds one; `paymentHistoryFileName` is the same
   * rule the host uses, kept in step by a test on both sides.
   */
  paymentHistoryCsv: (customerId: string, signal?: AbortSignal) =>
    api.getText(`/api/customers/${customerId}/documents/payment-history`, { signal }),

  /**
   * One customer's transition register (WP-2.15), newest first.
   *
   * Read on `customers.read`, unlike the five writes below: a clerk who may not move a customer
   * still has to be able to say what has happened to them — the call WP-2.12 made about the deposit
   * ledger for the same reason.
   */
  transitions: (customerId: string, filters: AccountTransitionFilters = {}, signal?: AbortSignal) =>
    api.get<AccountTransition[]>(`/api/customers/${customerId}/transitions`, {
      query: params({
        kind: filters.kind,
        serviceAccountId: filters.serviceAccountId,
        limit: filters.limit === undefined ? undefined : String(filters.limit),
      }),
      signal,
    }),

  /**
   * The five transitions (WP-2.15), each a POST sub-resource named for the act.
   *
   * All five need `customers.transition` on the host — narrower than the `customers.write` that
   * opened the page. There is no other way in: the old `POST /api/customers/{id}/status` and
   * `POST /api/service-accounts/{id}/close` were removed rather than left beside these, because a
   * second way in is a way without a reason code.
   */
  changeCustomerClass: (customerId: string, input: ChangeCustomerClassInput) =>
    api.post<AccountTransition>(`/api/customers/${customerId}/transitions/class`, { json: input }),

  changeCustomerStatus: (customerId: string, input: ChangeCustomerStatusInput) =>
    api.post<AccountTransition>(`/api/customers/${customerId}/transitions/status`, { json: input }),

  moveIn: (customerId: string, input: MoveInInput) =>
    api.post<AccountTransition>(`/api/customers/${customerId}/transitions/move-in`, { json: input }),

  moveOut: (customerId: string, input: MoveOutInput) =>
    api.post<AccountTransition>(`/api/customers/${customerId}/transitions/move-out`, { json: input }),

  transferService: (customerId: string, input: TransferServiceInput) =>
    api.post<AccountTransition>(`/api/customers/${customerId}/transitions/transfer`, { json: input }),

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
  contacts: (customerId: string) => ['customers', 'contacts', customerId] as const,
  profile: (customerId: string) => ['customers', 'profile', customerId] as const,
  deposits: (customerId: string) => ['customers', 'deposits', customerId] as const,
  depositAssessment: (customerId: string) => ['customers', 'deposit-assessment', customerId] as const,
  /**
   * Every note query for one customer, whatever it was narrowed by — what a write invalidates.
   *
   * A prefix of `notes` below, so invalidating this reaches the tab's unfiltered fetch and any
   * narrowed one at once. A write changes what belongs in every window, not just the open one.
   */
  notesFor: (customerId: string) => ['customers', 'notes', customerId] as const,
  notes: (customerId: string, filters: CustomerNoteFilters = {}) =>
    ['customers', 'notes', customerId, filters] as const,
  statement: (customerId: string, range: StatementRange) =>
    ['customers', 'statement', customerId, range] as const,

  /**
   * Every transition query for one customer, whatever it was narrowed by — what a write invalidates.
   *
   * A prefix of `transitions` below, so invalidating this reaches the tab's unfiltered fetch and any
   * narrowed one at once. A transition changes more than its own register, which is why the tab
   * invalidates the customer, the accounts and the deposit alongside it.
   */
  transitionsFor: (customerId: string) => ['customers', 'transitions', customerId] as const,
  transitions: (customerId: string, filters: AccountTransitionFilters = {}) =>
    ['customers', 'transitions', customerId, filters] as const,
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

/**
 * The contacts on one customer.
 *
 * Lives at the 360 page beside every other query rather than inside the contacts tab, which is the
 * call WP-2.10 made for all of them: switching to a tab issues no request, and each query still owns
 * its own loading and error state.
 */
export function useCustomerContacts(customerId: string | undefined) {
  return useQuery({
    queryKey: customerKeys.contacts(customerId ?? ''),
    queryFn: ({ signal }) => customersApi.contacts(customerId!, signal),
    enabled: Boolean(customerId),
  });
}

/**
 * One customer's deposit ledger.
 *
 * Lives at the 360 page beside every other query rather than inside the deposit tab, which is the
 * call WP-2.10 made for all of them: switching to a tab issues no request, and each query still owns
 * its own loading and error state.
 *
 * Always answers for a customer who exists — one who has never paid a deposit reads back as a zero
 * balance and no entries, which is an ordinary position rather than a missing record.
 */
export function useCustomerDeposits(customerId: string | undefined) {
  return useQuery({
    queryKey: customerKeys.deposits(customerId ?? ''),
    queryFn: ({ signal }) => customersApi.deposits(customerId!, signal),
    enabled: Boolean(customerId),
  });
}

/**
 * What the schedule asks of one customer today (WP-2.17), on demand.
 *
 * `enabled` off by default: the deposit tab already has the same figures inside the ledger, so
 * fetching this on page load would be a second request for an answer that is already there. What it
 * exists for is the re-ask — a rep presses "re-assess" after a class change or a new supply, and the
 * host recomputes against whatever the meters now say.
 */
export function useDepositAssessment(customerId: string | undefined, enabled = false) {
  return useQuery({
    queryKey: customerKeys.depositAssessment(customerId ?? ''),
    queryFn: ({ signal }) => customersApi.depositAssessment(customerId!, signal),
    enabled: Boolean(customerId) && enabled,
  });
}

/**
 * One customer's transition register (WP-2.15).
 *
 * Lives at the 360 page beside every other query, which is WP-2.10's rule: switching to a tab issues
 * no request. It is a plain read — the documents tab is the exception to that rule and it is the
 * exception because the host AUDITS a statement, which a transition list is not.
 */
export function useCustomerTransitions(customerId: string | undefined) {
  return useQuery({
    queryKey: customerKeys.transitions(customerId ?? ''),
    queryFn: ({ signal }) => customersApi.transitions(customerId!, {}, signal),
    enabled: Boolean(customerId),
  });
}

/**
 * One customer's note log.
 *
 * Lives at the 360 page beside every other query rather than inside the notes tab, which is the call
 * WP-2.10 made for all of them: switching to a tab issues no request, and each query still owns its
 * own loading and error state. The summary's pinned strip and the timeline's fifth source read the
 * same fetch, so the tab is not the only consumer either way.
 *
 * A customer who has never been rung reads back as an empty list, which is an ordinary position
 * rather than a missing record — the host's 404 is for a customer who does not exist.
 */
export function useCustomerNotes(customerId: string | undefined, filters: CustomerNoteFilters = {}) {
  return useQuery({
    queryKey: customerKeys.notes(customerId ?? '', filters),
    queryFn: ({ signal }) => customersApi.notes(customerId!, filters, signal),
    enabled: Boolean(customerId),
  });
}

/**
 * A customer's account statement over a range (WP-2.14).
 *
 * **Unlike every other query on the 360, this one does NOT live at the page.** Fetching it writes an
 * audit entry saying a statement was produced, so it runs when a rep asks for one — `enabled` is
 * what holds it — and never as a side effect of opening a tab. It is held for the session for the
 * same reason: a refetch on window focus would put a second "a statement went out" entry in the
 * trail because somebody switched tabs.
 */
export function useCustomerStatement(
  customerId: string | undefined,
  range: StatementRange,
  enabled: boolean,
) {
  return useQuery({
    queryKey: customerKeys.statement(customerId ?? '', range),
    queryFn: ({ signal }) => customersApi.statement(customerId!, range, signal),
    enabled: enabled && Boolean(customerId),
    staleTime: Infinity,
    refetchOnWindowFocus: false,
  });
}

/**
 * One customer's mailing address and communication preferences.
 *
 * Always answers: a customer nobody has saved a profile for reads back as the defaults with a null
 * `updatedAt`, so there is no "not found" case for a screen to handle.
 */
export function useCustomerProfile(customerId: string | undefined) {
  return useQuery({
    queryKey: customerKeys.profile(customerId ?? ''),
    queryFn: ({ signal }) => customersApi.profile(customerId!, signal),
    enabled: Boolean(customerId),
  });
}

/**
 * ---------------------------------------------------------------------------------------------
 * Service applications (WP-2.18)
 *
 * The reviewed path to a service account: an application is filed, a reviewer picks it up, the
 * required documents arrive, and approval is what opens the account. It lives in this client rather
 * than one of its own because it is the Customers module's — CONVENTIONS.md asks for one typed
 * client per module, and an `applications.ts` beside this file would be a second client for the
 * same schema.
 * ---------------------------------------------------------------------------------------------
 */

/** Mirrors `ServiceApplicationStatus`. The order is the lifecycle's, not the alphabet's. */
export const serviceApplicationStatuses = ['Submitted', 'UnderReview', 'Approved', 'Rejected', 'Withdrawn'] as const;
export type ServiceApplicationStatus = (typeof serviceApplicationStatuses)[number];

/** Mirrors `ServiceApplicationType` — which checklist an application is held to. */
export const serviceApplicationTypes = ['ResidentialConnection', 'CommercialConnection'] as const;
export type ServiceApplicationType = (typeof serviceApplicationTypes)[number];

/** Mirrors `ApplicationDocumentKind`. */
export const applicationDocumentKinds = ['PhotoId', 'ProofOfOccupancy', 'BusinessLicence', 'Other'] as const;
export type ApplicationDocumentKind = (typeof applicationDocumentKinds)[number];

/** Mirrors `ApplicationReasonCode`. */
export const applicationReasonCodes = [
  'Other',
  'DocumentsVerified',
  'ApprovedByException',
  'DocumentsIncomplete',
  'IdentityNotVerified',
  'OccupancyNotProven',
  'PremiseNotServiceable',
  'OutstandingBalance',
  'DuplicateApplication',
  'ApplicantWithdrew',
  'ApplicantUnreachable',
  'SupersededByAnotherApplication',
] as const;
export type ApplicationReasonCode = (typeof applicationReasonCodes)[number];

/** Mirrors `ApplicationChecklistResponse` — one line of what an application must carry. */
export type ApplicationChecklistLine = {
  kind: ApplicationDocumentKind;
  isSatisfied: boolean;
  documentId: string | null;
  uploadedAt: string | null;
};

/** Mirrors `ApplicationDocumentResponse`. Never the bytes — those have a route of their own. */
export type ApplicationDocument = {
  id: string;
  kind: ApplicationDocumentKind;
  fileName: string;
  contentType: string;
  sizeInBytes: number;
  checksum: string;
  uploadedAt: string;
  actorId: string;
  actorName: string | null;
};

/** Mirrors `ServiceApplicationResponse`. */
export type ServiceApplication = {
  id: string;
  applicationNumber: string;
  customerId: string;
  serviceLocationId: string;
  serviceType: ServiceType;
  type: ServiceApplicationType;
  status: ServiceApplicationStatus;
  allowedTransitions: ServiceApplicationStatus[];
  isOpen: boolean;
  requestedOn: string;
  notes: string | null;
  checklist: ApplicationChecklistLine[];
  missingDocuments: ApplicationDocumentKind[];
  isDocumentationComplete: boolean;
  documents: ApplicationDocument[];
  submittedAt: string;
  submittedById: string;
  submittedByName: string | null;
  reviewStartedAt: string | null;
  reviewerId: string | null;
  reviewerName: string | null;
  decidedAt: string | null;
  decidedById: string | null;
  decidedByName: string | null;
  decisionReasonCode: ApplicationReasonCode | null;
  decisionNotes: string | null;
  serviceAccountId: string | null;
  replacesApplicationId: string | null;
};

/** Mirrors `ApplicationApprovalResponse` — what an approval produced. */
export type ApplicationApproval = {
  application: ServiceApplication;
  account: ServiceAccount;
  deposit: DepositRequirement;
};

/** Mirrors `ApplicationTypeResponse`. */
export type ApplicationTypeReference = {
  type: ServiceApplicationType;
  requiredDocuments: ApplicationDocumentKind[];
};

/**
 * Mirrors `ApplicationReferenceResponse` — the host's own checklist, decision lists and upload
 * policy, projected so a browser never keeps a second copy of them to fall out of step with.
 */
export type ApplicationReference = {
  types: ApplicationTypeReference[];
  documentKinds: ApplicationDocumentKind[];
  allowedContentTypes: string[];
  maxSizeInBytes: number;
  reasonCodes: Record<string, ApplicationReasonCode[]>;
  reasonCodesRequiringNotes: ApplicationReasonCode[];
};

/** How the application register is narrowed. */
export type ServiceApplicationFilters = {
  search?: string;
  customerId?: string;
  serviceLocationId?: string;
  status?: ServiceApplicationStatus | '';
  serviceType?: ServiceType | '';
  openOnly?: boolean;
};

/** Mirrors `SubmitApplicationRequest`. `serviceType` is required, with no default — see the host. */
export type SubmitApplicationInput = {
  customerId: string;
  serviceLocationId: string;
  serviceType: ServiceType;
  requestedOn?: string | null;
  notes?: string | null;
};

/** Mirrors `DecideApplicationRequest` — one body for approve, reject and withdraw. */
export type DecideApplicationInput = {
  reasonCode: ApplicationReasonCode;
  notes?: string | null;
};

export const applicationsApi = {
  list: (filters: ServiceApplicationFilters = {}, signal?: AbortSignal) =>
    api.get<ServiceApplication[]>('/api/service-applications', {
      query: {
        ...params({
          search: filters.search,
          customerId: filters.customerId,
          serviceLocationId: filters.serviceLocationId,
          status: filters.status,
          serviceType: filters.serviceType,
        }),
        ...(filters.openOnly ? { openOnly: true } : {}),
        limit: registryWindow,
      },
      signal,
    }),

  get: (id: string, signal?: AbortSignal) =>
    api.get<ServiceApplication>(`/api/service-applications/${id}`, { signal }),

  /** The checklist, the decision lists and the upload policy. Reference data — safe to hold for a session. */
  reference: (signal?: AbortSignal) => api.get<ApplicationReference>('/api/service-application-reference', { signal }),

  submit: (input: SubmitApplicationInput) =>
    api.post<ServiceApplication>('/api/service-applications', { json: input }),

  /** Picks it up. The move a decision has to come after, so the queue can say who is dealing with what. */
  startReview: (id: string) =>
    api.post<ServiceApplication>(`/api/service-applications/${id}/review`, { json: {} }),

  /**
   * Attaches a scan. Multipart, not JSON: base64 in a body would inflate every scan by a third and
   * hide its size from the server until it had already been buffered.
   */
  attachDocument: (id: string, kind: ApplicationDocumentKind, file: File) => {
    const body = new FormData();
    body.append('kind', kind);
    body.append('file', file);

    return api.postForm<ApplicationDocument>(`/api/service-applications/${id}/documents`, body);
  },

  /**
   * Where a document's bytes are served from.
   *
   * A URL rather than a fetch, because the browser is what renders a PDF or an image and handing it
   * an object URL would mean downloading the scan into memory to show it. Needs
   * `customers.documents` on the host, which is narrower than the `customers.read` that opened the
   * page — so this link 403s for a clerk who may see the checklist but not the identity page behind
   * it, which is the intended behaviour and not a bug in the link.
   */
  documentUrl: (id: string, documentId: string) =>
    `${env.apiBaseUrl}/api/service-applications/${id}/documents/${documentId}/content`,

  /** Approval — the act that opens the account. Needs `customers.approve`. */
  approve: (id: string, input: DecideApplicationInput) =>
    api.post<ApplicationApproval>(`/api/service-applications/${id}/approve`, { json: input }),

  /** Refusal. Needs `customers.approve` too, and is terminal — the way forward is a resubmission. */
  reject: (id: string, input: DecideApplicationInput) =>
    api.post<ServiceApplication>(`/api/service-applications/${id}/reject`, { json: input }),

  /** The applicant's own act, relayed by the desk. Needs only `customers.write`. */
  withdraw: (id: string, input: DecideApplicationInput) =>
    api.post<ServiceApplication>(`/api/service-applications/${id}/withdraw`, { json: input }),

  /** A fresh application replacing a decided one. Carries what was applied for and none of the evidence. */
  resubmit: (id: string, input: { requestedOn?: string | null; notes?: string | null } = {}) =>
    api.post<ServiceApplication>(`/api/service-applications/${id}/resubmissions`, { json: input }),
};

export const applicationKeys = {
  all: ['service-applications'] as const,
  reference: () => ['service-applications', 'reference'] as const,
  list: (filters: ServiceApplicationFilters) => ['service-applications', 'list', filters] as const,
  detail: (id: string) => ['service-applications', 'detail', id] as const,
};

/**
 * The application register. Takes `enabled` so a panel whose subject has not resolved yet — the
 * 360° page before it knows which customer it is showing — asks for nothing rather than everything.
 */
export function useServiceApplications(filters: ServiceApplicationFilters, enabled = true) {
  return useQuery({
    queryKey: applicationKeys.list(filters),
    queryFn: ({ signal }) => applicationsApi.list(filters, signal),
    enabled,
  });
}

/** One application with its documents. */
export function useServiceApplication(id: string | undefined) {
  return useQuery({
    queryKey: applicationKeys.detail(id ?? ''),
    queryFn: ({ signal }) => applicationsApi.get(id!, signal),
    enabled: Boolean(id),
  });
}

/**
 * The checklist and decision lists the host declares. Reference data — it changes with a deployment,
 * never with a screen — so it is held for the session rather than re-fetched per application.
 */
export function useApplicationReference() {
  return useQuery({
    queryKey: applicationKeys.reference(),
    queryFn: ({ signal }) => applicationsApi.reference(signal),
    staleTime: Infinity,
  });
}
