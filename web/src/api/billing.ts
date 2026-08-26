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

/** Mirrors `BillResponse`. */
export type Bill = {
  id: string;
  billNumber: string;
  serviceAccountId: string;
  accountNumber: string;
  customerId: string;
  customerName: string;
  serviceLocationId: string;
  ratePlanId: string;
  ratePlanCode: string;
  ratePlanName: string;
  ratePlanEffectiveFrom: string;
  currency: string;
  unitOfMeasure: string;
  periodStart: string;
  periodEnd: string;
  cycleCode: string | null;
  meterReadingId: string;
  meterId: string;
  meterNumber: string;
  previousReading: number | null;
  currentReading: number | null;
  consumption: number;
  /** What the rate engine printed. Never moves again once the bill is calculated. */
  totalAmount: number;
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
  ratePlanCode: string;
  ratePlanName: string;
  ratePlanEffectiveFrom: string;
  currency: string;
  unitOfMeasure: string;
  periodStart: string;
  periodEnd: string;
  meterNumber: string;
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
