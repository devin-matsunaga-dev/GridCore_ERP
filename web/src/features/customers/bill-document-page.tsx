import { ArrowLeft, Printer } from 'lucide-react';
import { Link, useParams } from 'react-router';
import { useBillDocument } from '@/api/billing';
import { DetailList, orNotRecorded } from '@/components/registry/detail-list';
import { ErrorState } from '@/components/registry/error-state';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { StatusPill, toneFor } from '@/components/ui/status';
import { formatDate, formatMoney, formatQuantity } from '@/lib/format';

/**
 * A bill reproduced as the document it was issued as (WP-2.14).
 *
 * **A route rather than a drawer**, for two reasons. A rep can send a colleague the link to the
 * exact document they are looking at — the call the 360's tabs already made — and printing needs a
 * page: `print:` classes strip the shell around this card so ⌘P produces the bill rather than a
 * screenshot of an application.
 *
 * **Every figure on it came off a stored column.** Nothing is recalculated, because a reprint that
 * re-ran the rate engine would disagree with the paper in the customer's hand the first time a
 * tariff was corrected — and it is precisely the disputed bills that get reprinted. Corrections
 * since are listed beneath the document rather than folded into its lines, so the customer can
 * check the bill they hold and then read what has happened to it.
 *
 * Opening this page produces a copy, and the host records that it did.
 */
export function BillDocumentPage() {
  const { customerId, billId } = useParams<{ customerId: string; billId: string }>();
  const document = useBillDocument(billId);

  const back = (
    <Button variant="ghost" size="sm" className="-ml-3 print:hidden" asChild>
      <Link to={customerId ? `/customers/${customerId}/documents` : '/customers'}>
        <ArrowLeft aria-hidden="true" />
        Back to documents
      </Link>
    </Button>
  );

  if (document.isError) {
    return (
      <div className="space-y-6">
        {back}
        <Card>
          <ErrorState error={document.error} onRetry={() => void document.refetch()} />
        </Card>
      </div>
    );
  }

  if (document.isPending || !document.data) {
    return (
      <div className="space-y-6">
        {back}
        <Card>
          <CardContent className="space-y-3 pt-6">
            <Skeleton className="h-6 w-56" />
            <Skeleton className="h-4 w-full" />
            <Skeleton className="h-4 w-2/3" />
          </CardContent>
        </Card>
      </div>
    );
  }

  const bill = document.data;

  return (
    <div className="space-y-6">
      {back}

      <Card>
        <CardHeader className="flex-row items-start justify-between gap-4">
          <div className="space-y-1">
            <CardTitle>Bill {bill.billNumber}</CardTitle>
            <p className="text-muted text-[13px]">
              {formatDate(bill.periodStart)} to {formatDate(bill.periodEnd)} · issued {formatDate(bill.issuedOn)}
            </p>
          </div>

          <span className="flex items-center gap-3">
            <StatusPill status={bill.status} tone={toneFor(bill.status)} />
            <Button variant="secondary" size="sm" className="print:hidden" onClick={() => window.print()}>
              <Printer aria-hidden="true" />
              Print
            </Button>
          </span>
        </CardHeader>

        <CardContent className="space-y-6">
          <DetailList
            columns={2}
            items={[
              // The name AS BILLED, not the customer's name today. A customer who has since married
              // still had this bill sent to the name printed on it, and a reprint that quietly
              // updated it would be a different document.
              { label: 'Billed to', value: bill.customerName },
              { label: 'Account', value: <span className="tabular">{bill.accountNumber}</span> },
              { label: 'Meter', value: <span className="tabular">{bill.meterNumber}</span> },
              {
                label: 'Tariff',
                value: `${bill.ratePlanName} (${bill.ratePlanCode}, from ${formatDate(bill.ratePlanEffectiveFrom)})`,
              },
              {
                label: 'Readings',
                value: (
                  <span className="tabular">
                    {orNotRecorded(bill.previousReading !== null && formatQuantity(bill.previousReading))} →{' '}
                    {orNotRecorded(bill.currentReading !== null && formatQuantity(bill.currentReading))}
                  </span>
                ),
              },
              {
                label: 'Consumption',
                value: (
                  <span className="tabular">
                    {formatQuantity(bill.consumption)} {bill.unitOfMeasure}
                  </span>
                ),
              },
              { label: 'Due', value: orNotRecorded(bill.dueDate && formatDate(bill.dueDate)) },
            ]}
          />

          <div className="overflow-x-auto scrollbar-subtle">
            <table className="w-full min-w-[36rem] text-sm">
              <caption className="sr-only">What the bill charged, as issued</caption>
              <thead>
                <tr className="border-border border-b text-left text-[13px] font-medium text-muted">
                  <th scope="col" className="py-2 pr-3">Charge</th>
                  <th scope="col" className="py-2 pr-3 text-right">Units</th>
                  <th scope="col" className="py-2 pr-3 text-right">Rate</th>
                  <th scope="col" className="py-2 text-right">Amount</th>
                </tr>
              </thead>

              <tbody>
                {bill.lines.map((line) => (
                  <tr key={line.sequence} className="border-border border-b">
                    <td className="py-2.5 pr-3 text-body">{line.description}</td>
                    <td className="py-2.5 pr-3 text-right tabular">
                      {line.units === null ? '—' : formatQuantity(line.units)}
                    </td>
                    <td className="py-2.5 pr-3 text-right tabular">
                      {line.ratePerUnit === null ? '—' : formatMoney(line.ratePerUnit)}
                    </td>
                    <td className="py-2.5 text-right tabular">{formatMoney(line.amount)}</td>
                  </tr>
                ))}

                <tr>
                  <th scope="row" className="py-2.5 pr-3 text-left font-semibold text-heading" colSpan={3}>
                    Total as issued
                  </th>
                  <td className="py-2.5 text-right font-semibold tabular">{formatMoney(bill.printedTotal)}</td>
                </tr>
              </tbody>
            </table>
          </div>

          {bill.corrections.length > 0 && (
            <section className="space-y-3" aria-labelledby="bill-corrections-heading">
              <h3 id="bill-corrections-heading" className="text-heading text-base font-semibold">
                Corrections since this bill was issued
              </h3>

              {/*
                Beneath the document, never inside it. WP-2.4's rule read forwards: a credit is its
                own dated entry, and netting it into the consumption line it relates to would produce
                a bill that has never existed and that the customer cannot reconcile against theirs.
              */}
              <ul className="divide-border divide-y">
                {bill.corrections.map((correction) => (
                  <li key={correction.sequence} className="flex flex-wrap items-baseline justify-between gap-3 py-2.5">
                    <span className="space-y-0.5">
                      <span className="flex items-center gap-2">
                        <StatusPill
                          status={correction.kind}
                          tone={correction.kind === 'Credit' ? 'success' : 'warning'}
                        />
                        <span className="text-body text-sm">{correction.reason}</span>
                      </span>
                      <span className="block text-muted text-[13px]">
                        {formatDate(correction.recordedAt)}
                        {correction.actorName && ` · ${correction.actorName}`}
                      </span>
                    </span>

                    <span className="text-heading tabular">{formatMoney(correction.amount)}</span>
                  </li>
                ))}
              </ul>
            </section>
          )}

          <dl className="grid gap-x-6 gap-y-2 border-border border-t pt-4 text-sm sm:grid-cols-3">
            <Figure label="Owed after corrections" value={bill.amountDue} />
            <Figure label="Paid" value={bill.amountPaid} />
            <Figure label="Outstanding" value={bill.balance} />
          </dl>

          <p className="text-muted text-[13px]">
            Copy produced {formatDate(bill.producedAt)} by {bill.producedByName ?? bill.producedById}.
            All amounts in {bill.currency}.
          </p>
        </CardContent>
      </Card>
    </div>
  );
}

function Figure({ label, value }: { label: string; value: number }) {
  return (
    <div className="flex items-baseline justify-between gap-3 sm:block">
      <dt className="text-muted text-[13px]">{label}</dt>
      <dd className="text-heading font-medium tabular">{formatMoney(value)}</dd>
    </div>
  );
}
