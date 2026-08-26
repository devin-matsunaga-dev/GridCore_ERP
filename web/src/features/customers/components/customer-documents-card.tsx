import { Download, FileText, Printer } from 'lucide-react';
import { useState } from 'react';
import { Link } from 'react-router';
import type { Bill } from '@/api/billing';
import { customersApi, useCustomerStatement, type AccountStatement, type StatementRange } from '@/api/customers';
import { toast } from '@/components/feedback/toast';
import { EmptyState } from '@/components/registry/empty-state';
import { ErrorState } from '@/components/registry/error-state';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Skeleton } from '@/components/ui/skeleton';
import { StatusPill } from '@/components/ui/status';
import { formatDate, formatMoney } from '@/lib/format';
import {
  billsThatCanBeReprinted,
  defaultStatementRange,
  downloadCsv,
  isStatementRangeValid,
  paymentHistoryFileName,
  statementKindLabel,
  statementKindTone,
  statementProvesOut,
  statementTouchesDeposit,
} from '../documents';

/**
 * The documents a rep hands or sends a customer (WP-2.14): an account statement, a payment-history
 * export, and the way in to a bill reprint.
 *
 * **Nothing here is fetched until it is asked for**, which is the opposite of every other tab on
 * this page. The 360's rule is that queries live at the page so switching tabs issues no request —
 * right for reads, and wrong for these: the host AUDITS a statement and an export, because both
 * leave the building. A tab that produced a statement by being opened would put an entry in the
 * trail saying a document went to a customer who never asked for one.
 *
 * The statement panel is the printable one. `print:` classes strip the app around it, so ⌘P from
 * this screen produces the document rather than a screenshot of a web page — which is what
 * WORK_PACKAGES.md's "read-side, rendered" means in practice.
 */
export function CustomerDocumentsCard({
  customerId,
  accountNumber,
  bills,
  isBillsLoading,
  billsError,
  onRetryBills,
}: {
  customerId: string;
  accountNumber: string;
  bills: readonly Bill[];
  isBillsLoading: boolean;
  billsError: unknown;
  onRetryBills: () => void;
}) {
  // The range the two boxes hold, and the range the statement was asked for. They are separate on
  // purpose: typing in a date box must not fire an audited request per keystroke, so nothing is
  // asked for until "Produce statement" is pressed.
  const [range, setRange] = useState<StatementRange>(() => defaultStatementRange(new Date()));
  const [asked, setAsked] = useState<StatementRange | null>(null);
  const [isExporting, setIsExporting] = useState(false);

  const statement = useCustomerStatement(customerId, asked ?? range, asked !== null);
  const isRangeValid = isStatementRangeValid(range);

  async function exportPaymentHistory() {
    setIsExporting(true);

    try {
      const csv = await customersApi.paymentHistoryCsv(customerId);

      if (downloadCsv(paymentHistoryFileName(accountNumber, new Date()), csv)) {
        toast.success('Payment history exported.');
      } else {
        toast.error('This browser cannot save the file.');
      }
    } catch (error) {
      toast.apiError(error, 'The payment history could not be exported.');
    } finally {
      setIsExporting(false);
    }
  }

  return (
    <div className="space-y-6">
      <Card className="print:hidden">
        <CardHeader>
          <CardTitle>Account statement</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <p className="text-body text-sm">
            Every bill, payment, correction and deposit movement over a range, opening balance to
            closing balance. Producing one is recorded against the account.
          </p>

          <div className="flex flex-wrap items-end gap-3">
            <div className="space-y-1.5">
              <Label htmlFor="statement-from">From</Label>
              <Input
                id="statement-from"
                type="date"
                value={range.from}
                max={range.to}
                onChange={(event) => setRange({ ...range, from: event.target.value })}
              />
            </div>

            <div className="space-y-1.5">
              <Label htmlFor="statement-to">To</Label>
              <Input
                id="statement-to"
                type="date"
                value={range.to}
                min={range.from}
                onChange={(event) => setRange({ ...range, to: event.target.value })}
              />
            </div>

            <Button
              disabled={!isRangeValid || statement.isFetching}
              title={isRangeValid ? undefined : 'A statement cannot end before it starts.'}
              onClick={() => setAsked({ ...range })}
            >
              <FileText aria-hidden="true" />
              {asked === null ? 'Produce statement' : 'Produce again'}
            </Button>

            <Button variant="secondary" disabled={isExporting} onClick={() => void exportPaymentHistory()}>
              <Download aria-hidden="true" />
              {isExporting ? 'Exporting…' : 'Export payment history'}
            </Button>
          </div>
        </CardContent>
      </Card>

      {asked !== null && (
        <StatementPanel
          statement={statement.data}
          isLoading={statement.isPending || statement.isFetching}
          error={statement.isError ? statement.error : undefined}
          onRetry={() => void statement.refetch()}
        />
      )}

      <Card className="print:hidden">
        <CardHeader>
          <CardTitle>Bill reprints</CardTitle>
        </CardHeader>
        <CardContent>
          {billsError ? (
            <ErrorState error={billsError} onRetry={onRetryBills} />
          ) : isBillsLoading ? (
            <div className="space-y-3">
              <Skeleton className="h-4 w-full" />
              <Skeleton className="h-4 w-2/3" />
            </div>
          ) : (
            <BillReprintList customerId={customerId} bills={bills} />
          )}
        </CardContent>
      </Card>
    </div>
  );
}

/**
 * The statement itself — the printable half.
 *
 * A table, because it is a register of like rows, and the running balance is the column a customer
 * reads down. `tabular-nums` and right alignment on every numeric column, per DESIGN.md: figures
 * that do not line up are figures somebody adds up wrong.
 */
function StatementPanel({
  statement,
  isLoading,
  error,
  onRetry,
}: {
  statement: AccountStatement | undefined;
  isLoading: boolean;
  error: unknown;
  onRetry: () => void;
}) {
  if (error) {
    return (
      <Card>
        <ErrorState error={error} onRetry={onRetry} />
      </Card>
    );
  }

  if (isLoading || !statement) {
    return (
      <Card>
        <CardContent className="space-y-3 pt-6">
          <Skeleton className="h-6 w-64" />
          <Skeleton className="h-4 w-full" />
          <Skeleton className="h-4 w-full" />
          <Skeleton className="h-4 w-2/3" />
        </CardContent>
      </Card>
    );
  }

  const showsDeposit = statementTouchesDeposit(statement);

  return (
    <Card>
      <CardHeader className="flex-row items-start justify-between gap-4">
        <div className="space-y-1">
          <CardTitle>Statement of account</CardTitle>
          <p className="text-muted text-[13px]">
            {formatDate(statement.from)} to {formatDate(statement.to)} · {statement.accountNumber}
          </p>
        </div>

        <Button variant="secondary" size="sm" className="print:hidden" onClick={() => window.print()}>
          <Printer aria-hidden="true" />
          Print
        </Button>
      </CardHeader>

      <CardContent className="space-y-5">
        <div className="text-body text-sm">
          <p className="text-heading font-medium">{statement.customerName}</p>
          {statement.mailingAddress && <p>{statement.mailingAddress}</p>}
        </div>

        {/*
          The host refuses to compose a statement whose lines disagree with its own totals, so this
          can only fire if the browser and the host disagree about what a line MEANS. Said out loud
          rather than swallowed: a document about money that quietly does not add up is worse than
          one that says so.
        */}
        {!statementProvesOut(statement) && (
          <p role="alert" className="rounded-lg bg-danger-soft px-3 py-2 text-sm text-danger">
            This statement does not add up and must not be sent. Report it before going any further.
          </p>
        )}

        {statement.isTruncated && (
          <p role="alert" className="rounded-lg bg-warning-soft px-3 py-2 text-sm text-warning">
            This account has more history than one statement can carry, so the opening balance may be
            short. Ask for a later start date.
          </p>
        )}

        <div className="overflow-x-auto scrollbar-subtle">
          <table className="w-full min-w-[44rem] text-sm">
            <thead>
              <tr className="border-border border-b text-left text-[13px] font-medium text-muted">
                <th scope="col" className="py-2 pr-3">Date</th>
                <th scope="col" className="py-2 pr-3">Detail</th>
                <th scope="col" className="py-2 pr-3">Reference</th>
                <th scope="col" className="py-2 pr-3 text-right">Charges / credits</th>
                {showsDeposit && <th scope="col" className="py-2 pr-3 text-right">Deposit</th>}
                <th scope="col" className="py-2 text-right">Balance</th>
              </tr>
            </thead>

            <tbody>
              <tr className="border-border border-b">
                <td className="py-2.5 pr-3 text-muted">{formatDate(statement.from)}</td>
                <td className="py-2.5 pr-3 font-medium text-heading" colSpan={2}>Opening balance</td>
                <td className="py-2.5 pr-3" />
                {showsDeposit && (
                  <td className="py-2.5 pr-3 text-right tabular">{formatMoney(statement.openingDepositHeld)}</td>
                )}
                <td className="py-2.5 text-right font-medium tabular">{formatMoney(statement.openingBalance)}</td>
              </tr>

              {statement.entries.map((entry) => (
                <tr key={`${entry.occurredAt}-${entry.kind}-${entry.reference ?? ''}`} className="border-border border-b">
                  <td className="py-2.5 pr-3 whitespace-nowrap text-muted">{formatDate(entry.date)}</td>
                  <td className="py-2.5 pr-3">
                    <span className="flex flex-wrap items-center gap-2">
                      <StatusPill status={statementKindLabel(entry.kind)} tone={statementKindTone(entry.kind)} />
                      <span className="text-body">{entry.description}</span>
                    </span>
                  </td>
                  <td className="py-2.5 pr-3 text-muted tabular">
                    {/*
                      The bill a line concerns is a link to its reprint. A statement is where a rep
                      is standing when a customer says "what was that charge in July" — the answer is
                      the document itself, one click away.
                    */}
                    {entry.billId ? (
                      <Link to={`/customers/${statement.customerId}/bills/${entry.billId}`} className="text-primary hover:underline">
                        {entry.reference ?? 'Bill'}
                      </Link>
                    ) : (
                      (entry.reference ?? '—')
                    )}
                  </td>
                  <td className="py-2.5 pr-3 text-right tabular">
                    {entry.amount === 0 ? '—' : formatMoney(entry.amount)}
                  </td>
                  {showsDeposit && (
                    <td className="py-2.5 pr-3 text-right tabular">
                      {entry.depositAmount === 0 ? '—' : formatMoney(entry.depositAmount)}
                    </td>
                  )}
                  <td className="py-2.5 text-right tabular">{formatMoney(entry.balanceAfter)}</td>
                </tr>
              ))}

              <tr>
                <td className="py-2.5 pr-3 text-muted">{formatDate(statement.to)}</td>
                <td className="py-2.5 pr-3 font-semibold text-heading" colSpan={2}>Closing balance</td>
                <td className="py-2.5 pr-3" />
                {showsDeposit && (
                  <td className="py-2.5 pr-3 text-right font-semibold tabular">{formatMoney(statement.closingDepositHeld)}</td>
                )}
                <td className="py-2.5 text-right font-semibold tabular">{formatMoney(statement.closingBalance)}</td>
              </tr>
            </tbody>
          </table>
        </div>

        {statement.entries.length === 0 && (
          <p className="text-muted text-sm">
            Nothing happened on this account in this period. The balance is carried across unchanged.
          </p>
        )}

        <dl className="grid gap-x-6 gap-y-2 border-border border-t pt-4 text-sm sm:grid-cols-2 lg:grid-cols-4">
          <SummaryFigure label="Billed" value={statement.billed} />
          <SummaryFigure label="Corrections" value={statement.corrected} />
          <SummaryFigure label="Paid" value={statement.paid} />
          <SummaryFigure label="Deposit applied" value={statement.depositApplied} />
        </dl>

        <p className="text-muted text-[13px]">
          Produced {formatDate(statement.producedAt)} by {statement.producedByName ?? statement.producedById}.
          All amounts in {statement.currency}.
        </p>
      </CardContent>
    </Card>
  );
}

function SummaryFigure({ label, value }: { label: string; value: number }) {
  return (
    <div className="flex items-baseline justify-between gap-3 sm:block">
      <dt className="text-muted text-[13px]">{label}</dt>
      <dd className="text-heading font-medium tabular">{formatMoney(value)}</dd>
    </div>
  );
}

/**
 * The bills a rep can hand a customer a copy of.
 *
 * Drafts are absent — the host refuses to reproduce one, because a draft is not a document anybody
 * was sent. A row is a link rather than a button: the document has its own URL, so a rep can send a
 * colleague the exact bill they are looking at.
 */
function BillReprintList({ customerId, bills }: { customerId: string; bills: readonly Bill[] }) {
  const reprintable = billsThatCanBeReprinted(bills);

  if (reprintable.length === 0) {
    return (
      <EmptyState
        icon={FileText}
        title="No issued bills"
        message="Nothing has been billed to this customer yet, so there is nothing to reprint."
      />
    );
  }

  return (
    <ul className="divide-border divide-y">
      {reprintable.map((bill) => (
        <li key={bill.id}>
          <Link
            to={`/customers/${customerId}/bills/${bill.id}`}
            className="flex flex-wrap items-center justify-between gap-3 py-3 hover:bg-canvas"
          >
            <span className="flex flex-col">
              <span className="text-heading font-medium tabular">{bill.billNumber}</span>
              <span className="text-muted text-[13px]">
                {formatDate(bill.periodStart)} to {formatDate(bill.periodEnd)}
                {bill.issuedOn && ` · issued ${formatDate(bill.issuedOn)}`}
              </span>
            </span>

            <span className="flex items-center gap-3">
              <span className="text-heading tabular">{formatMoney(bill.totalAmount)}</span>
              <StatusPill status={bill.status} />
            </span>
          </Link>
        </li>
      ))}
    </ul>
  );
}
