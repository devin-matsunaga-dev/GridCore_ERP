import { describe, expect, it } from 'vitest';
import { accountStatement, statementEntry } from '@/test/registry-fixtures';
import { bill } from '@/test/revenue-cycle-fixtures';
import {
  billsThatCanBeReprinted,
  defaultStatementRange,
  downloadCsv,
  isStatementRangeValid,
  isoDate,
  paymentHistoryFileName,
  statementKindLabel,
  statementKindTone,
  statementProvesOut,
  statementTouchesDeposit,
} from './documents';

describe('statementProvesOut', () => {
  it('accepts a statement whose lines add up to its closing balance', () =>
    // The default fixture is a real statement: opening 0, one bill of 120, closing 120.
    expect(statementProvesOut(accountStatement())).toBe(true));

  it('rejects one whose closing balance disagrees with its own lines', () => {
    // The host refuses to compose this, so it can only arrive if the two sides disagree about what
    // a line MEANS — which is exactly the drift worth catching on the screen rather than in a
    // customer's hands.
    const wrong = accountStatement({ closingBalance: 200 });

    expect(statementProvesOut(wrong)).toBe(false);
  });

  it('carries an unchanged balance across a period with no activity', () =>
    expect(statementProvesOut(accountStatement({
      entries: [],
      openingBalance: 120,
      closingBalance: 120,
      billed: 0,
    }))).toBe(true));

  it('adds the lines up in cents', () => {
    // Three lines that sum to 0.30 in decimal arithmetic and to 0.30000000000000004 in floating
    // point. A statement told it does not balance because of that is a statement a rep stops
    // trusting.
    const statement = accountStatement({
      openingBalance: 0,
      closingBalance: 0.3,
      entries: [
        statementEntry({ amount: 0.1, balanceAfter: 0.1 }),
        statementEntry({ amount: 0.2, balanceAfter: 0.3 }),
      ],
    });

    expect(statementProvesOut(statement)).toBe(true);
  });
});

describe('statementTouchesDeposit', () => {
  it('leaves the deposit column off an account that has never had one', () =>
    // A column of dashes is a column that makes a document harder to read. Most residential
    // customers never pay a deposit at all.
    expect(statementTouchesDeposit(accountStatement())).toBe(false));

  it('shows it when a deposit moved in the period', () =>
    expect(statementTouchesDeposit(accountStatement({
      entries: [statementEntry({ kind: 'DepositCollected', amount: 0, depositAmount: 75, depositHeldAfter: 75 })],
    }))).toBe(true));

  it('shows it when a deposit was already held before the period', () =>
    // Nothing moved in July, but the utility is holding 75 of the customer's money and a statement
    // that did not say so would be one they ring about.
    expect(statementTouchesDeposit(accountStatement({ entries: [], openingDepositHeld: 75 }))).toBe(true));
});

describe('statementKindLabel', () => {
  it('names each line in the words a customer would use', () => {
    expect(statementKindLabel('BillIssued')).toBe('Bill issued');
    expect(statementKindLabel('PaymentReceived')).toBe('Payment received');
    expect(statementKindLabel('DepositCollected')).toBe('Deposit received');
  });

  it('does not colour a bill as a failure and does colour a withdrawal as one to look at', () => {
    // A bill is not bad news, it is Tuesday. A withdrawal is the line somebody looks for when a
    // balance moved without a payment behind it.
    expect(statementKindTone('BillIssued')).toBe('info');
    expect(statementKindTone('PaymentReceived')).toBe('success');
    expect(statementKindTone('BillWithdrawn')).toBe('warning');
  });
});

describe('billsThatCanBeReprinted', () => {
  it('leaves DRAFTS out', () => {
    // The host answers a 409 for a draft — it is not a document anybody was sent. A list offering
    // one is a list whose choices produce errors.
    const reprintable = billsThatCanBeReprinted([
      bill({ id: 'draft', status: 'Draft', issuedOn: null }),
      bill({ id: 'issued', status: 'Issued', issuedOn: '2026-07-05' }),
    ]);

    expect(reprintable.map((row) => row.id)).toEqual(['issued']);
  });

  it('offers a cancelled or paid bill, because both were sent', () => {
    const reprintable = billsThatCanBeReprinted([
      bill({ id: 'paid', status: 'Paid', issuedOn: '2026-06-05' }),
      bill({ id: 'cancelled', status: 'Cancelled', issuedOn: '2026-05-05' }),
    ]);

    expect(reprintable).toHaveLength(2);
  });

  it('puts the newest bill first, which is the one being asked about', () => {
    const reprintable = billsThatCanBeReprinted([
      bill({ id: 'may', issuedOn: '2026-05-05' }),
      bill({ id: 'july', issuedOn: '2026-07-05' }),
      bill({ id: 'june', issuedOn: '2026-06-05' }),
    ]);

    expect(reprintable.map((row) => row.id)).toEqual(['july', 'june', 'may']);
  });

  it('leaves the list it was given alone', () => {
    const bills = [bill({ id: 'may', issuedOn: '2026-05-05' }), bill({ id: 'july', issuedOn: '2026-07-05' })];

    billsThatCanBeReprinted(bills);

    expect(bills.map((row) => row.id)).toEqual(['may', 'july']);
  });
});

describe('defaultStatementRange', () => {
  it('opens on the last quarter, which is what the host defaults to', () => {
    const range = defaultStatementRange(new Date('2026-08-26T10:00:00Z'));

    expect(range.to).toBe('2026-08-26');
    expect(range.from).toBe('2026-05-28');
  });

  it('produces a range the host would accept', () =>
    expect(isStatementRangeValid(defaultStatementRange(new Date('2026-08-26T10:00:00Z')))).toBe(true));
});

describe('isStatementRangeValid', () => {
  it('refuses a range that runs backwards before the request is made', () =>
    // The host refuses it too, with a 400. Catching it here is what stops a rep pressing a button
    // to be told what the two boxes in front of them already said.
    expect(isStatementRangeValid({ from: '2026-07-31', to: '2026-07-01' })).toBe(false));

  it('accepts a single day', () =>
    expect(isStatementRangeValid({ from: '2026-07-01', to: '2026-07-01' })).toBe(true));

  it('refuses an empty box', () =>
    expect(isStatementRangeValid({ from: '', to: '2026-07-31' })).toBe(false));
});

describe('paymentHistoryFileName', () => {
  it('names the file after the account and the day it was run', () =>
    // The same rule the host puts in its Content-Disposition header. They are asserted on both
    // sides, because a fetched file has no name of its own — the header does not survive
    // `Response.text()`.
    expect(paymentHistoryFileName('C-000001', new Date('2026-08-26T10:00:00Z')))
      .toBe('payment-history-C-000001-2026-08-26.csv'));

  it('carries nothing a path could read', () => {
    expect(paymentHistoryFileName('C/000123', new Date('2026-08-26T10:00:00Z')))
      .toBe('payment-history-C-000123-2026-08-26.csv');
    expect(paymentHistoryFileName('', new Date('2026-08-26T10:00:00Z')))
      .toBe('payment-history-account-2026-08-26.csv');
  });
});

describe('downloadCsv', () => {
  it('puts the byte-order mark back that the fetch stripped', async () => {
    // THE POINT OF THIS FUNCTION. The host serves the file with a UTF-8 BOM and `Response.text()`
    // strips it as it decodes, so writing the text straight back out gives a clerk a spreadsheet
    // with every accented place name mangled.
    const blobs: Blob[] = [];
    const original = URL.createObjectURL;

    // The two statics, never the whole `URL`: replacing the global takes `new URL()` with it, and
    // half this application builds a request with that.
    URL.createObjectURL = ((blob: Blob) => {
      blobs.push(blob);
      return 'blob:stub';
    }) as typeof URL.createObjectURL;
    URL.revokeObjectURL = (() => {}) as typeof URL.revokeObjectURL;

    expect(downloadCsv('history.csv', 'Number,Customer\r\nPAY-000001,Ana Cruz\r\n')).toBe(true);

    expect(blobs).toHaveLength(1);

    // Read as BYTES, not through `Blob.text()`: that decodes as UTF-8, which strips a leading BOM —
    // so the one assertion that matters here is the one `.text()` cannot make.
    const bytes = new Uint8Array(await blobs[0].arrayBuffer());

    expect(Array.from(bytes.slice(0, 3))).toEqual([0xef, 0xbb, 0xbf]);
    expect(new TextDecoder().decode(bytes.slice(3))).toBe('Number,Customer\r\nPAY-000001,Ana Cruz\r\n');

    URL.createObjectURL = original;
  });

  it('answers false rather than throwing where a browser cannot save', () => {
    // A test environment, mostly — jsdom implements no object URLs at all. The caller says so with a
    // toast rather than leaving a rep looking at a button that did nothing.
    const original = URL.createObjectURL;

    (URL as { createObjectURL?: unknown }).createObjectURL = undefined;

    expect(downloadCsv('history.csv', 'a,b')).toBe(false);

    URL.createObjectURL = original;
  });
});

describe('isoDate', () => {
  it('formats a date as the host DateOnly wants it', () =>
    expect(isoDate(new Date('2026-08-26T23:30:00Z'))).toBe('2026-08-26'));
});
