import { useQuery } from '@tanstack/react-query';
import { api } from './client';

/**
 * The Billing module's client — bills, the runs that raise them and the act that makes one owed.
 * One typed client per module (CONVENTIONS.md); components never call `fetch` and never build a
 * URL themselves.
 *
 * Only what the revenue-cycle walk and the 360° page need is here. The billing registry screens
 * (the adjustment form, the AR worklist, the overdue review) belong to the work package that
 * builds them.
 */

/** Mirrors `BillStatus`. The order is the lifecycle's, not the alphabet's. */
export const billStatuses = ['Draft', 'Issued', 'PartiallyPaid', 'Overdue', 'Paid', 'Cancelled'] as const;
export type BillStatus = (typeof billStatuses)[number];

/** Mirrors `BillLineResponse`. */
export type BillLine = {
  sequence: number;
  /** The standing charge, or a consumption block from one tier of the tariff. */
  kind: string;
  description: string;
  tierSequence: number | null;
  units: number | null;
  ratePerUnit: number | null;
  amount: number;
};

/** Mirrors `BillAdjustmentResponse`. */
export type BillAdjustment = {
  id: string;
  sequence: number;
  kind: 'Credit' | 'Charge';
  /** The signed change to what is owed — negative on a credit. */
  amount: number;
  amountDueAfter: number;
  reason: string;
  actorId: string;
  actorName: string | null;
  recordedAt: string;
};

/**
 * What a bill was raised for. A `Charge` bill carries fees alone — no meter, no tariff, no period of
 * supply — which is why every one of those fields is nullable above.
 */
export type BillKind = 'Consumption' | 'Charge';

/** Mirrors `BillResponse`. */
export type Bill = {
  id: string;
  billNumber: string;
  serviceAccountId: string;
  accountNumber: string;
  customerId: string;
  customerName: string;
  serviceLocationId: string;
  /** A period of supply, or fees alone — a charge bill has no meter and no tariff. */
  kind: BillKind;
  ratePlanId: string | null;
  ratePlanCode: string | null;
  ratePlanName: string | null;
  ratePlanEffectiveFrom: string | null;
  currency: string;
  unitOfMeasure: string | null;
  periodStart: string;
  periodEnd: string;
  cycleCode: string | null;
  meterReadingId: string | null;
  meterId: string | null;
  meterNumber: string | null;
  previousReading: number | null;
  currentReading: number | null;
  consumption: number;
  /** What the rate engine printed. Never moves again once the bill is calculated. */
  totalAmount: number;
  /** How much of that is fees from the published schedule rather than supply (WP-2.16). */
  feeAmount: number;
  /** The signed sum of the corrections made since — WP-2.4's immutable entries. */
  adjustmentTotal: number;
  /** What is owed today: the printed total plus those corrections. */
  amountDue: number;
  amountPaid: number;
  /** What is still owed — `amountDue - amountPaid`, never `totalAmount - amountPaid`. */
  balance: number;
  status: BillStatus;
  allowedTransitions: BillStatus[];
  isOutstanding: boolean;
  issuedOn: string | null;
  dueDate: string | null;
  paidAt: string | null;
  statusReason: string | null;
  createdAt: string;
  actorId: string;
  actorName: string | null;
  /** Empty on a list row; a run result and the detail endpoint fill them in. */
  lines: BillLine[];
  adjustments: BillAdjustment[];
};

/** Mirrors `SkippedReadingResponse` — a reading the run passed over, and why, in words. */
export type SkippedReading = {
  meterReadingId: string;
  serviceLocationId: string;
  meterNumber: string;
  reason: string;
};

/** Mirrors `BillingRunResponse`. */
export type BillingRun = {
  cycleCode: string;
  raised: number;
  totalBilled: number;
  skippedCount: number;
  byReason: Record<string, number>;
  /** Every bill raised, as a DRAFT — a run publishes nothing. */
  bills: Bill[];
  skipped: SkippedReading[];
};

/** How a bill list is narrowed. Mirrors `BillQuery`, minus the parts no screen asks for yet. */
export type BillFilters = {
  customerId?: string;
  serviceAccountId?: string;
  /** Only money still owed — the AR worklist, without naming three statuses. */
  outstandingOnly?: boolean;
  limit?: number;
  /**
   * Load each row's corrections too. Off by default on the host, because a register page of fifty
   * bills does not want them; the 360° timeline asks for a handful of bills and shows a correction
   * as an event of its own, so it does.
   */
  includeAdjustments?: boolean;
};

/** Mirrors `BillDocumentLineResponse` — one line of the bill, exactly as it was printed. */
export type BillDocumentLine = {
  sequence: number;
  kind: string;
  description: string;
  tierSequence: number | null;
  units: number | null;
  ratePerUnit: number | null;
  amount: number;
};

/** Mirrors `BillDocumentCorrectionResponse` — a correction shown BENEATH the document, never in it. */
export type BillDocumentCorrection = {
  sequence: number;
  kind: 'Credit' | 'Charge';
  amount: number;
  amountDueAfter: number;
  reason: string;
  actorName: string | null;
  recordedAt: string;
};

/**
 * Mirrors `BillDocumentResponse` — an issued bill reproduced as the document the customer was sent
 * (WP-2.14).
 *
 * Not a `Bill`. The overlap is large and the difference is the point: every figure here came off a
 * stored column rather than being recalculated, `printedTotal` is what the paper in the customer's
 * hand says whatever has happened since, and the corrections are a separate list rather than a
 * number folded into the lines.
 */
export type BillDocument = {
  billId: string;
  billNumber: string;
  serviceAccountId: string;
  accountNumber: string;
  customerId: string;
  /** The name the bill was RAISED in, which is not necessarily the customer's name today. */
  customerName: string;
  serviceLocationId: string;
  /** A period of supply, or fees alone — a charge bill has no meter and no tariff. */
  kind: BillKind;
  ratePlanCode: string | null;
  ratePlanName: string | null;
  ratePlanEffectiveFrom: string | null;
  currency: string;
  unitOfMeasure: string | null;
  periodStart: string;
  periodEnd: string;
  meterNumber: string | null;
  previousReading: number | null;
  currentReading: number | null;
  consumption: number;
  lines: BillDocumentLine[];
  /** What the document said. The lines add up to exactly this. */
  printedTotal: number;
  corrections: BillDocumentCorrection[];
  correctionTotal: number;
  amountDue: number;
  amountPaid: number;
  balance: number;
  status: BillStatus;
  issuedOn: string;
  dueDate: string | null;
  producedAt: string;
  producedById: string;
  producedByName: string | null;
};

export const billingApi = {
  get: (id: string, signal?: AbortSignal) => api.get<Bill>(`/api/bills/${id}`, { signal }),

  list: (filters: BillFilters, signal?: AbortSignal) =>
    api.get<Bill[]>('/api/bills', { query: { ...filters }, signal }),

  /**
   * Bills a reading cycle. Produces drafts and publishes nothing: issuing is the separate act that
   * makes a bill money the utility is owed. Like the reading run, this is a batch over the whole
   * cycle — there is no per-account form of it.
   */
  run: (cycleCode: string) =>
    api.post<BillingRun>('/api/bills/runs', { json: { cycleCode }, timeoutMs: 60_000 }),

  /** Issues a draft. This is what publishes `BillIssued`, and so what reaches Finance. */
  issue: (id: string) => api.post<Bill>(`/api/bills/${id}/issue`, { json: {} }),

  /**
   * The bill as the document it was issued as (WP-2.14).
   *
   * A GET that the host AUDITS: a copy of a bill leaves the building, and "who sent this out, for
   * whom, and when" is the question asked of it afterwards. So this is not something to call
   * speculatively — it is fetched when a rep asks to see the document, and never as part of loading
   * a page. Needs `customers.documents` on the host, which is narrower than the `billing.read` that
   * lists bills.
   */
  document: (id: string, signal?: AbortSignal) =>
    api.get<BillDocument>(`/api/bills/${id}/document`, { signal }),
};

export const billKeys = {
  all: ['bills'] as const,
  list: (filters: BillFilters) => ['bills', 'list', filters] as const,
  detail: (id: string) => ['bills', 'detail', id] as const,
  document: (id: string) => ['bills', 'document', id] as const,
};

/**
 * A window of bills. Takes `enabled` so a panel whose subject has not resolved yet — the 360° page
 * before it knows which customer it is showing — asks for nothing rather than for everything.
 */
export function useBills(filters: BillFilters, enabled = true) {
  return useQuery({
    queryKey: billKeys.list(filters),
    queryFn: ({ signal }) => billingApi.list(filters, signal),
    enabled,
  });
}

/**
 * One bill's document.
 *
 * **`enabled` is load-bearing here in a way it is nowhere else.** Fetching this writes an audit
 * entry saying a copy of the bill was produced, so it must run when a rep asks for the document and
 * at no other time — never on mount, never for a row the page merely listed. The reprint route
 * passes the id it was opened with.
 */
export function useBillDocument(billId: string | undefined) {
  return useQuery({
    queryKey: billKeys.document(billId ?? ''),
    queryFn: ({ signal }) => billingApi.document(billId!, signal),
    enabled: Boolean(billId),

    // Never silently re-produced. A refetch on window focus would put a second "a copy went out"
    // entry in the audit trail because somebody switched tabs.
    staleTime: Infinity,
    refetchOnWindowFocus: false,
  });
}

/**
 * ---------------------------------------------------------------------------------------------
 * Fee schedule and account charges (WP-2.16)
 *
 * The non-consumption half of what a utility charges for. WP-2.16 shipped the whole of this
 * server-side and no screen at all, on the stated grounds that the desk which raises a fee belongs
 * with the workflow that raises one. WP-2.18 is the first of those workflows to land, so it pays
 * the debt.
 * ---------------------------------------------------------------------------------------------
 */

/** Mirrors `FeeCode` — the published catalogue, in the order the host declares it. */
export const feeCodes = [
  'ServiceConnection',
  'Reconnection',
  'ReturnedPayment',
  'MeterTest',
  'Inspection',
  'UnauthorizedConnection',
  'LateCharge',
] as const;
export type FeeCode = (typeof feeCodes)[number];

/**
 * Mirrors `FeeBasis` (WP-2.19) — how a published fee arrives at its figure.
 *
 * A `Flat` fee is what the schedule says, whatever the account owes. A `Rate` fee is a percentage of
 * something the register computes, which today is the 1%-a-month late charge. The distinction is
 * what stops a rate fee ever appearing in the counter's fee picker: it has no amount until something
 * is charged on it, and nothing a rep could type would be anything but an invented balance.
 */
export const feeBases = ['Flat', 'Rate'] as const;
export type FeeBasis = (typeof feeBases)[number];

/** Mirrors `AccountChargeStatus`. `Billed` is terminal — correcting one is an adjustment to its bill. */
export const accountChargeStatuses = ['Pending', 'Billed', 'Cancelled'] as const;
export type AccountChargeStatus = (typeof accountChargeStatuses)[number];

/** Mirrors `FeeScheduleResponse` — one published fee, priced for the day asked about. */
export type FeeScheduleEntry = {
  code: FeeCode;
  name: string;
  description: string;
  serviceType: string;
  basis: FeeBasis;
  /** Null on a rate fee, which has no figure until something is charged on it. */
  amount: number | null;
  /** The published rate as a fraction — `0.01` for one per cent. Null on a flat fee. */
  rate: number | null;
  currency: string;
  effectiveFrom: string;
  feeScheduleId: string;
};

/** Mirrors `AccountChargeResponse`. */
export type AccountCharge = {
  id: string;
  serviceAccountId: string;
  accountNumber: string;
  customerId: string;
  customerName: string;
  code: FeeCode;
  description: string;
  basis: FeeBasis;
  /** The rate it was taken at, on a rate fee (WP-2.19). Null on a flat one. */
  rate: number | null;
  /** What that rate was taken on — the past-due balance, for a late charge. Null on a flat one. */
  basisAmount: number | null;
  amount: number;
  currency: string;
  feeScheduleId: string;
  scheduleEffectiveFrom: string;
  raisedOn: string;
  reason: string;
  status: AccountChargeStatus;
  allowedTransitions: AccountChargeStatus[];
  isPending: boolean;
  billId: string | null;
  billNumber: string | null;
  raisedAt: string;
  statusChangedAt: string;
  statusReason: string | null;
  actorId: string;
  actorName: string | null;
};

/** Mirrors `CounterBillResponse` — a charge put on a bill of its own and issued in one act. */
export type CounterBill = {
  charge: AccountCharge;
  bill: Bill;
};

/** How the charge register is narrowed. */
export type AccountChargeFilters = {
  serviceAccountId?: string;
  customerId?: string;
  status?: AccountChargeStatus | '';
  pendingOnly?: boolean;
};

/** Mirrors `RaiseChargeRequest`. A reason is required — this is the sensitive act invariant 5 is about. */
export type RaiseChargeInput = {
  serviceAccountId: string;
  code: FeeCode;
  reason: string;
  raisedOn?: string | null;
};

export const feesApi = {
  /**
   * The catalogue as it stands on a day. Read-only on the host and deliberately so: changing $135 to
   * $150 is an effective-dated row in a migration, never an endpoint pointed at production.
   *
   * `on` is what makes a reprint honest — asking for a past day returns the figure that was
   * published then, not today's.
   */
  schedule: (on?: string, signal?: AbortSignal) =>
    api.get<FeeScheduleEntry[]>('/api/fee-schedule', { query: on ? { on } : undefined, signal }),

  charges: (filters: AccountChargeFilters = {}, signal?: AbortSignal) =>
    api.get<AccountCharge[]>('/api/account-charges', {
      query: {
        ...Object.fromEntries(
          Object.entries({
            serviceAccountId: filters.serviceAccountId,
            customerId: filters.customerId,
            status: filters.status,
          }).filter(([, value]) => value !== undefined && value !== ''),
        ),
        ...(filters.pendingOnly ? { pendingOnly: true } : {}),
      },
      signal,
    }),

  /** Raises a published fee against an account. Needs `billing.charge`, which the front desk holds. */
  raise: (input: RaiseChargeInput) => api.post<AccountCharge>('/api/account-charges', { json: input }),

  /** Withdraws a charge that has not reached a bill. A reason is required — it removes money owed. */
  cancel: (id: string, reason: string) =>
    api.post<AccountCharge>(`/api/account-charges/${id}/cancel`, { json: { reason } }),

  /** Puts a pending charge on a bill of its own and issues it, so the customer can pay it now. */
  billNow: (id: string, reason?: string | null) =>
    api.post<CounterBill>(`/api/account-charges/${id}/bill`, { json: { reason } }),
};

export const feeKeys = {
  schedule: (on?: string) => ['fee-schedule', on ?? 'today'] as const,
  charges: (filters: AccountChargeFilters) => ['account-charges', 'list', filters] as const,
};

/**
 * The published fee schedule for today.
 *
 * Reference data — it moves by migration — so it is held for the session. Not `Infinity` like the
 * deposit schedule, though: this one is priced *for a day*, and a browser left open across midnight
 * would otherwise quote yesterday's figure at a counter.
 */
export function useFeeSchedule(on?: string) {
  return useQuery({
    queryKey: feeKeys.schedule(on),
    queryFn: ({ signal }) => feesApi.schedule(on, signal),
    staleTime: 60 * 60 * 1000,
  });
}

/** The charges raised against a customer or one of their accounts. */
export function useAccountCharges(filters: AccountChargeFilters, enabled = true) {
  return useQuery({
    queryKey: feeKeys.charges(filters),
    queryFn: ({ signal }) => feesApi.charges(filters, signal),
    enabled,
  });
}
