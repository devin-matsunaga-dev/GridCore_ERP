import { useQuery } from '@tanstack/react-query';
import { api } from './client';

/**
 * The Payments module's client — taking money against a bill through the payment provider, and
 * reading back what was taken. One typed client per module (CONVENTIONS.md); components never call
 * `fetch` and never build a URL themselves.
 */

/** Mirrors `PaymentMethods`. The wire values are the host's, not display labels. */
export const paymentMethods = ['card', 'bank-transfer', 'cash'] as const;
export type PaymentMethod = (typeof paymentMethods)[number];

/** How each method reads on screen. Sentence case, per DESIGN.md. */
export const paymentMethodLabels: Record<PaymentMethod, string> = {
  card: 'Card',
  'bank-transfer': 'Bank transfer',
  cash: 'Cash',
};

/** Mirrors `PaymentStatus`. */
export const paymentStatuses = ['Pending', 'Approved', 'Declined', 'Failed', 'Refunded'] as const;
export type PaymentStatus = (typeof paymentStatuses)[number];

/**
 * Mirrors `PaymentOutcome` — what the provider answered.
 *
 * An outcome is not a status: `Declined` and `InsufficientFunds` both land on the `Declined`
 * status, while `Timeout` lands on `Failed`, because the money may have moved and the answer been
 * lost. That distinction is the host's and this client does not flatten it.
 */
export const paymentOutcomes = ['Approved', 'Declined', 'InsufficientFunds', 'Timeout', 'Refunded'] as const;
export type PaymentOutcome = (typeof paymentOutcomes)[number];

/** Mirrors `PaymentResponse`. */
export type Payment = {
  id: string;
  paymentNumber: string;
  serviceAccountId: string;
  accountNumber: string;
  customerId: string;
  customerName: string;
  billId: string;
  billNumber: string;
  amount: number;
  currency: string;
  method: string;
  /** The instrument charged, as the utility is allowed to hold it. Never a full card number. */
  instrument: string | null;
  balanceBefore: number;
  status: PaymentStatus;
  outcome: PaymentOutcome | null;
  allowedTransitions: PaymentStatus[];
  /** Whether the utility actually holds this money. */
  isSettled: boolean;
  providerName: string | null;
  providerReference: string | null;
  providerMessage: string | null;
  requestedAt: string;
  settledAt: string | null;
  statusReason: string | null;
  actorId: string;
  actorName: string | null;
};

/** Mirrors `TakePaymentResponse`. */
export type TakePaymentResult = {
  payment: Payment;
  /** Whether the money moved. The one field a caller branches on. */
  approved: boolean;
  billNumber: string;
  balanceBefore: number;
};

/** Mirrors `TakePaymentRequest`. */
export type TakePaymentInput = {
  billId: string;
  amount: number;
  method: PaymentMethod;
  instrument?: string | null;
};

/** How a payment list is narrowed. Mirrors `PaymentQuery`, minus the parts no screen asks for. */
export type PaymentFilters = {
  customerId?: string;
  serviceAccountId?: string;
  billId?: string;
  /** Only money the utility actually holds. Off, on the 360° page: a refusal is the answer. */
  settledOnly?: boolean;
  limit?: number;
};

export const paymentsApi = {
  list: (filters: PaymentFilters, signal?: AbortSignal) =>
    api.get<Payment[]>('/api/payments', { query: { ...filters }, signal }),

  /**
   * Takes a payment against a bill.
   *
   * A refusal comes back as a **200 with `approved: false`**, not a 4xx: the attempt happened, it
   * is a row in the register, and it is the answer to "why does this customer still owe money".
   * The real 4xx paths are a bill that does not exist, one nobody owes, and an amount larger than
   * the balance or finer than a cent.
   */
  take: (input: TakePaymentInput) => api.post<TakePaymentResult>('/api/payments', { json: input }),
};

export const paymentKeys = {
  all: ['payments'] as const,
  list: (filters: PaymentFilters) => ['payments', 'list', filters] as const,
  detail: (id: string) => ['payments', 'detail', id] as const,
};

/**
 * A window of payments. Takes `enabled` for the same reason `useBills` does — a panel whose
 * subject has not resolved yet asks for nothing rather than for every payment in the register.
 */
export function usePayments(filters: PaymentFilters, enabled = true) {
  return useQuery({
    queryKey: paymentKeys.list(filters),
    queryFn: ({ signal }) => paymentsApi.list(filters, signal),
    enabled,
  });
}
