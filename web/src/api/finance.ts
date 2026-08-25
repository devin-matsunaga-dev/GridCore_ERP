import { useQuery } from '@tanstack/react-query';
import { api } from './client';

/**
 * The Finance module's client — the general ledger, read-only, which is structural rather than an
 * omission: the ledger's only author is the event seam, so there is nothing here to post with.
 * One typed client per module (CONVENTIONS.md).
 *
 * Only what the revenue-cycle walk needs is here — the entries one account caused, its receivables
 * row and the trial balance. WP-4.2 owns the Finance views and extends this client rather than
 * replacing it.
 */

/** Mirrors `AccountType`. */
export type AccountType = 'Asset' | 'Liability' | 'Equity' | 'Revenue' | 'Expense';

/** Which side an account is normally increased on. */
export type NormalBalance = 'Debit' | 'Credit';

/** Mirrors `JournalLineResponse`. */
export type JournalLine = {
  sequence: number;
  accountCode: string;
  accountName: string;
  accountType: AccountType;
  /** Zero on a credit line — the magnitude always goes on the correct side, never a negative. */
  debit: number;
  credit: number;
};

/** Mirrors `JournalEntryResponse`. */
export type JournalEntry = {
  id: string;
  entryNumber: string;
  /** The event that caused it. Null only on an entry no event raised. */
  eventId: string | null;
  source: string;
  reference: string;
  description: string;
  currency: string;
  /** The accounting date — the event's own, never the clock's. */
  postedOn: string;
  occurredAt: string;
  /** When Finance actually wrote it. */
  postedAt: string;
  serviceAccountId: string | null;
  customerId: string | null;
  totalDebits: number;
  totalCredits: number;
  /** True of every entry that exists — an unbalanced one is refused before it is written. */
  isBalanced: boolean;
  lines: JournalLine[];
  /** `system` for a posting made by a consumer, which is every posting today. */
  actorId: string;
  actorName: string | null;
};

/** Mirrors `TrialBalanceRowResponse`. */
export type TrialBalanceRow = {
  accountCode: string;
  accountName: string;
  accountType: AccountType;
  normalBalance: NormalBalance;
  debits: number;
  credits: number;
  /** Signed by the account's normal side, so an ordinary month is all positives. */
  balance: number;
  lineCount: number;
};

/** Mirrors `TrialBalanceResponse`. */
export type TrialBalance = {
  asOf: string;
  /** Every account in the chart, including the untouched ones. */
  rows: TrialBalanceRow[];
  totalDebits: number;
  totalCredits: number;
  /** How far out of balance the ledger is. Zero, unless something is wrong. */
  difference: number;
  isBalanced: boolean;
};

/** Mirrors `ReceivableRowResponse`. */
export type ReceivableRow = {
  serviceAccountId: string | null;
  customerId: string | null;
  charged: number;
  settled: number;
  /** What is still owed. Negative is money held on account. */
  outstanding: number;
  postingCount: number;
  lastPostedOn: string;
};

/** Mirrors `ReceivablesResponse`. */
export type Receivables = {
  asOf: string;
  controlAccountCode: string;
  rows: ReceivableRow[];
  totalCharged: number;
  totalSettled: number;
  /** The control account's balance — the subsidiary ledger reconciles with it by construction. */
  totalOutstanding: number;
  /** Receivables postings that named no party. Zero, today — reported rather than assumed away. */
  unallocated: number;
};

export type JournalFilters = {
  source?: string;
  reference?: string;
  serviceAccountId?: string;
  customerId?: string;
};

export type ReceivablesFilters = {
  serviceAccountId?: string;
  customerId?: string;
  outstandingOnly?: boolean;
};

/** Drops the empty selections, exactly as the other module clients do. */
function params(filters: Record<string, string | boolean | undefined>): Record<string, string | boolean> {
  return Object.fromEntries(
    Object.entries(filters).filter(([, value]) => value !== undefined && value !== ''),
  ) as Record<string, string | boolean>;
}

export const financeApi = {
  listJournalEntries: (filters: JournalFilters, signal?: AbortSignal) =>
    api.get<JournalEntry[]>('/api/finance/journal-entries', { query: params({ ...filters }), signal }),

  trialBalance: (signal?: AbortSignal) => api.get<TrialBalance>('/api/finance/trial-balance', { signal }),

  receivables: (filters: ReceivablesFilters, signal?: AbortSignal) =>
    api.get<Receivables>('/api/finance/accounts-receivable', { query: params({ ...filters }), signal }),
};

export const financeKeys = {
  all: ['finance'] as const,
  journal: (filters: JournalFilters) => ['finance', 'journal-entries', filters] as const,
  trialBalance: () => ['finance', 'trial-balance'] as const,
  receivables: (filters: ReceivablesFilters) => ['finance', 'accounts-receivable', filters] as const,
};

export function useJournalEntries(filters: JournalFilters, enabled = true) {
  return useQuery({
    queryKey: financeKeys.journal(filters),
    queryFn: ({ signal }) => financeApi.listJournalEntries(filters, signal),
    enabled,
  });
}

export function useTrialBalance(enabled = true) {
  return useQuery({
    queryKey: financeKeys.trialBalance(),
    queryFn: ({ signal }) => financeApi.trialBalance(signal),
    enabled,
  });
}

export function useReceivables(filters: ReceivablesFilters, enabled = true) {
  return useQuery({
    queryKey: financeKeys.receivables(filters),
    queryFn: ({ signal }) => financeApi.receivables(filters, signal),
    enabled,
  });
}
