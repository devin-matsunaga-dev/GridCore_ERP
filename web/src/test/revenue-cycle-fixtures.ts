import type { Bill, BillDocument, BillingRun } from '@/api/billing';
import type { Receivables, TrialBalance, JournalEntry } from '@/api/finance';
import type { MeterReading, ReadingCycle } from '@/api/metering';
import type { Payment, TakePaymentResult } from '@/api/payments';
import { customer, meter, serviceAccount, serviceLocation } from './registry-fixtures';

/**
 * The rows the revenue-cycle walk produces, shaped exactly as the host returns them. In the seeded
 * demo world — Rota — like the registry fixtures beside them, so a test reads like the app.
 *
 * The figures are one consistent bill: 447 units on RES-STD comes to $63.62, and every fixture
 * here agrees with that unless a test deliberately pulls one out of step.
 */

/** What the demonstration bill comes to. Every other figure here is derived from it. */
export const billTotal = 63.62;

export function meterReading(overrides: Partial<MeterReading> = {}): MeterReading {
  return {
    id: '0192f000-0000-7000-8000-000000000901',
    meterId: meter().id,
    serviceLocationId: serviceLocation().id,
    readingDate: '2026-08-25T00:30:00+00:00',
    reading: 15_267.5,
    source: 'Provider',
    previousReading: 14_820.5,
    previousReadingDate: '2026-07-25T00:30:00+00:00',
    consumption: 447,
    days: 31,
    dailyConsumption: 14.419,
    rolledOver: false,
    exceptionCode: 'None',
    isException: false,
    cycleCode: 'DEMO-20260825-0930',
    note: null,
    actorId: 'demo:reader',
    actorName: 'Wes Store (demo)',
    recordedAt: '2026-08-25T00:30:00+00:00',
    ...overrides,
  };
}

export function readingCycle(overrides: Partial<ReadingCycle> = {}): ReadingCycle {
  return {
    cycleCode: 'DEMO-20260825-0930',
    readAt: '2026-08-25T00:30:00+00:00',
    seed: 4471,
    provider: 'Simulated meter reading provider',
    recorded: 24,
    exceptions: 2,
    byExceptionCode: { HighUsage: 1, ZeroUsage: 1 },
    readings: [meterReading()],
    ...overrides,
  };
}

export function bill(overrides: Partial<Bill> = {}): Bill {
  return {
    id: '0192f000-0000-7000-8000-000000000a01',
    billNumber: 'BIL-000001',
    serviceAccountId: serviceAccount().id,
    accountNumber: serviceAccount().accountNumber,
    customerId: customer().id,
    customerName: customer().name,
    serviceLocationId: serviceLocation().id,
    kind: 'Consumption',
    ratePlanId: '0192f000-0000-7000-8000-000000000b01',
    ratePlanCode: 'RES-STD',
    ratePlanName: 'Residential standard',
    ratePlanEffectiveFrom: '2026-07-01',
    currency: 'USD',
    unitOfMeasure: 'kWh',
    periodStart: '2026-07-25',
    periodEnd: '2026-08-25',
    cycleCode: 'DEMO-20260825-0930',
    meterReadingId: meterReading().id,
    meterId: meter().id,
    meterNumber: meter().meterNumber,
    previousReading: 14_820.5,
    currentReading: 15_267.5,
    consumption: 447,
    totalAmount: billTotal,
    feeAmount: 0,
    adjustmentTotal: 0,
    amountDue: billTotal,
    amountPaid: 0,
    balance: billTotal,
    status: 'Issued',
    allowedTransitions: ['PartiallyPaid', 'Overdue', 'Paid', 'Cancelled'],
    isOutstanding: true,
    issuedOn: '2026-08-25',
    dueDate: '2026-09-14',
    paidAt: null,
    statusReason: null,
    createdAt: '2026-08-25T00:30:00+00:00',
    actorId: 'demo:billing',
    actorName: 'Wes Store (demo)',
    lines: [
      {
        sequence: 1,
        kind: 'StandingCharge',
        description: 'Standing charge',
        tierSequence: null,
        units: null,
        ratePerUnit: null,
        amount: 13.75,
      },
      {
        sequence: 2,
        kind: 'Consumption',
        description: 'First 500 kWh at 0.1225',
        tierSequence: 1,
        units: 447,
        ratePerUnit: 0.1225,
        amount: 49.87,
      },
    ],
    adjustments: [],
    ...overrides,
  };
}

/** The bill once the payment consumer has settled it. */
export function paidBill(overrides: Partial<Bill> = {}): Bill {
  return bill({
    status: 'Paid',
    amountPaid: billTotal,
    balance: 0,
    isOutstanding: false,
    allowedTransitions: [],
    paidAt: '2026-08-25T01:00:00+00:00',
    ...overrides,
  });
}

/**
 * A bill reproduced as the document it was issued as (WP-2.14).
 *
 * Deliberately an ADJUSTED bill: `printedTotal` is what the customer holds a copy of, the credit
 * sits in `corrections` as its own dated row, and `amountDue` is the two together. A fixture with no
 * corrections would let the screen's whole reason for existing go untested.
 */
export function billDocument(overrides: Partial<BillDocument> = {}): BillDocument {
  const issued = bill();

  return {
    billId: issued.id,
    billNumber: issued.billNumber,
    serviceAccountId: issued.serviceAccountId,
    accountNumber: issued.accountNumber,
    customerId: issued.customerId,
    customerName: issued.customerName,
    serviceLocationId: issued.serviceLocationId,
    kind: issued.kind,
    ratePlanCode: issued.ratePlanCode,
    ratePlanName: issued.ratePlanName,
    ratePlanEffectiveFrom: issued.ratePlanEffectiveFrom,
    currency: issued.currency,
    unitOfMeasure: issued.unitOfMeasure,
    periodStart: issued.periodStart,
    periodEnd: issued.periodEnd,
    meterNumber: issued.meterNumber,
    previousReading: issued.previousReading,
    currentReading: issued.currentReading,
    consumption: issued.consumption,
    lines: issued.lines,
    printedTotal: issued.totalAmount,
    corrections: [
      {
        sequence: 1,
        kind: 'Credit',
        amount: -10,
        amountDueAfter: issued.totalAmount - 10,
        reason: 'Meter misread',
        actorName: 'Ana Cruz (demo)',
        recordedAt: '2026-08-26T09:00:00+00:00',
      },
    ],
    correctionTotal: -10,
    amountDue: issued.totalAmount - 10,
    amountPaid: 0,
    balance: issued.totalAmount - 10,
    status: 'Issued',
    issuedOn: '2026-08-25',
    dueDate: '2026-09-15',
    producedAt: '2026-08-26T10:00:00+00:00',
    producedById: 'demo:customer-service',
    producedByName: 'Ana Cruz (demo)',
    ...overrides,
  };
}

export function billingRun(overrides: Partial<BillingRun> = {}): BillingRun {
  return {
    cycleCode: 'DEMO-20260825-0930',
    raised: 21,
    totalBilled: 1_482.19,
    skippedCount: 3,
    byReason: {
      'Reading is on the exception worklist (HighUsage)': 1,
      'No open service account at the premise': 2,
    },
    bills: [bill({ status: 'Draft', issuedOn: null, dueDate: null, allowedTransitions: ['Issued', 'Cancelled'] })],
    skipped: [],
    ...overrides,
  };
}

export function payment(overrides: Partial<Payment> = {}): Payment {
  return {
    id: '0192f000-0000-7000-8000-000000000c01',
    paymentNumber: 'PAY-000001',
    serviceAccountId: serviceAccount().id,
    accountNumber: serviceAccount().accountNumber,
    customerId: customer().id,
    customerName: customer().name,
    billId: bill().id,
    billNumber: bill().billNumber,
    amount: billTotal,
    currency: 'USD',
    method: 'card',
    instrument: '•••• 4242',
    balanceBefore: billTotal,
    status: 'Approved',
    outcome: 'Approved',
    allowedTransitions: ['Refunded'],
    isSettled: true,
    providerName: 'Payment sandbox',
    providerReference: 'SIM-4A19C2',
    providerMessage: 'Approved',
    requestedAt: '2026-08-25T01:00:00+00:00',
    settledAt: '2026-08-25T01:00:00+00:00',
    statusReason: null,
    actorId: 'demo:cashier',
    actorName: 'Wes Store (demo)',
    ...overrides,
  };
}

export function takePaymentResult(overrides: Partial<TakePaymentResult> = {}): TakePaymentResult {
  return {
    payment: payment(),
    approved: true,
    billNumber: bill().billNumber,
    balanceBefore: billTotal,
    ...overrides,
  };
}

/** A refusal: a 200 with a recorded row, never a 4xx. */
export function declinedPaymentResult(): TakePaymentResult {
  return takePaymentResult({
    payment: payment({
      status: 'Declined',
      outcome: 'Declined',
      isSettled: false,
      allowedTransitions: [],
      instrument: '•••• 0002',
      providerMessage: 'The card was declined.',
      settledAt: null,
    }),
    approved: false,
  });
}

export function journalEntry(overrides: Partial<JournalEntry> = {}): JournalEntry {
  return {
    id: '0192f000-0000-7000-8000-000000000d01',
    entryNumber: 'JRN-000001',
    eventId: '0192f000-0000-7000-8000-000000000e01',
    source: 'billing.bill_issued',
    reference: bill().billNumber,
    description: `Bill ${bill().billNumber} issued`,
    currency: 'USD',
    postedOn: '2026-08-25',
    occurredAt: '2026-08-25T00:30:00+00:00',
    postedAt: '2026-08-25T00:30:01+00:00',
    serviceAccountId: serviceAccount().id,
    customerId: customer().id,
    totalDebits: billTotal,
    totalCredits: billTotal,
    isBalanced: true,
    lines: [
      { sequence: 1, accountCode: '1100', accountName: 'Accounts receivable', accountType: 'Asset', debit: billTotal, credit: 0 },
      { sequence: 2, accountCode: '4000', accountName: 'Electricity revenue', accountType: 'Revenue', debit: 0, credit: billTotal },
    ],
    actorId: 'system',
    actorName: null,
    ...overrides,
  };
}

/** The cash receipt Finance posts when the payment approval reaches it. */
export function cashReceiptEntry(): JournalEntry {
  return journalEntry({
    id: '0192f000-0000-7000-8000-000000000d02',
    entryNumber: 'JRN-000002',
    source: 'payments.payment_approved',
    reference: payment().paymentNumber,
    description: `Payment ${payment().paymentNumber} received`,
    lines: [
      { sequence: 1, accountCode: '1000', accountName: 'Cash at bank', accountType: 'Asset', debit: billTotal, credit: 0 },
      { sequence: 2, accountCode: '1100', accountName: 'Accounts receivable', accountType: 'Asset', debit: 0, credit: billTotal },
    ],
  });
}

/** Receivables after the bill was issued and settled in full. */
export function receivables(overrides: Partial<Receivables> = {}): Receivables {
  return {
    asOf: '2026-08-25',
    controlAccountCode: '1100',
    rows: [
      {
        serviceAccountId: serviceAccount().id,
        customerId: customer().id,
        charged: billTotal,
        settled: billTotal,
        outstanding: 0,
        postingCount: 2,
        lastPostedOn: '2026-08-25',
      },
    ],
    totalCharged: billTotal,
    totalSettled: billTotal,
    totalOutstanding: 0,
    unallocated: 0,
    ...overrides,
  };
}

export function trialBalance(overrides: Partial<TrialBalance> = {}): TrialBalance {
  return {
    asOf: '2026-08-25',
    rows: [
      { accountCode: '1000', accountName: 'Cash at bank', accountType: 'Asset', normalBalance: 'Debit', debits: billTotal, credits: 0, balance: billTotal, lineCount: 1 },
      { accountCode: '1100', accountName: 'Accounts receivable', accountType: 'Asset', normalBalance: 'Debit', debits: billTotal, credits: billTotal, balance: 0, lineCount: 2 },
      { accountCode: '4000', accountName: 'Electricity revenue', accountType: 'Revenue', normalBalance: 'Credit', debits: 0, credits: billTotal, balance: billTotal, lineCount: 1 },
    ],
    totalDebits: billTotal * 2,
    totalCredits: billTotal * 2,
    difference: 0,
    isBalanced: true,
    ...overrides,
  };
}
