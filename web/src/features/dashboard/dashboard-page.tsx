import { Link } from 'react-router';
import { ArrowRight, RefreshCw } from 'lucide-react';
import {
  Card,
  CardAction,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from '@/components/ui/card';
import { Select } from '@/components/ui/select';
import { StatusDot } from '@/components/ui/status';
import { formatDateTime, formatMoneyCompact, formatPercent } from '@/lib/format';
import { cn } from '@/lib/utils';
import { AlertList } from './components/alert-list';
import { GridMap } from './components/grid-map';
import { KpiCard } from './components/kpi-card';
import { QuickActions } from './components/quick-actions';
import { SpendChart } from './components/spend-chart';
import { WorkOrderDonut } from './components/work-order-donut';
import { WorkOrderFeed } from './components/work-order-feed';
import {
  alerts,
  dataAsOf,
  gridStatus,
  kpis,
  procurementSpend,
  procurementTotals,
  quickActions,
  territories,
  workOrderFeed,
  workOrdersByStatus,
} from './demo-data';

/**
 * The home dashboard — the canonical reference look (docs/Design.png) rendered from the shell's
 * own components. The figures come from `demo-data.ts`; WP-4.3 wires them to real queries.
 */
export function DashboardPage() {
  return (
    <div className="space-y-6">
      <section aria-label="Key performance indicators" className="grid gap-6 sm:grid-cols-[repeat(2,minmax(0,1fr))] xl:grid-cols-[repeat(5,minmax(0,1fr))]">
        {kpis.map((kpi) => (
          <KpiCard key={kpi.label} {...kpi} />
        ))}
      </section>

      <section className="grid gap-6 lg:grid-cols-[repeat(2,minmax(0,1fr))] 2xl:grid-cols-[repeat(3,minmax(0,1fr))]">
        <Card className="flex flex-col">
          <CardHeader>
            <div>
              <CardTitle>System Overview</CardTitle>
              <CardDescription>Live grid status</CardDescription>
            </div>
            <CardAction>
              <Select options={territories} aria-label="Service territory" />
            </CardAction>
          </CardHeader>

          <CardContent className="flex flex-1 flex-wrap items-center gap-5">
            <GridMap className="h-40 min-w-0 flex-1" />
            <ul className="space-y-3.5">
              {gridStatus.map((status) => (
                <li key={status.label}>
                  <StatusDot tone={status.tone} className="text-body items-start text-[13px]">
                    <span className="mt-[-3px] block">
                      <span className="text-heading block font-medium">{status.label}</span>
                      <span className="tabular text-muted">{status.value}</span>
                    </span>
                  </StatusDot>
                </li>
              ))}
            </ul>
          </CardContent>

          <FooterLink to="/monitoring" label="View full map" />
        </Card>

        <Card className="flex flex-col">
          <CardHeader>
            <div>
              <CardTitle>Work Orders</CardTitle>
              <CardDescription>By status</CardDescription>
            </div>
          </CardHeader>
          <CardContent className="flex-1">
            <WorkOrderDonut slices={workOrdersByStatus} />
          </CardContent>
          <FooterLink to="/work-orders" label="View all work orders" />
        </Card>

        <Card className="lg:col-span-2 2xl:col-span-1">
          <CardHeader>
            <CardTitle>Alerts</CardTitle>
            <CardAction>
              <Link to="/monitoring" className="text-primary text-[13px] font-medium hover:underline">
                View all
              </Link>
            </CardAction>
          </CardHeader>
          <CardContent>
            <AlertList alerts={alerts} />
          </CardContent>
        </Card>
      </section>

      <section className="grid gap-6 lg:grid-cols-[repeat(2,minmax(0,1fr))] 2xl:grid-cols-[minmax(0,1.08fr)_minmax(0,1fr)_minmax(0,1fr)]">
        <WorkOrderFeed rows={workOrderFeed} />

        <Card className="flex flex-col">
          <CardHeader>
            <CardTitle>
              Procurement Spend <span className="text-muted font-normal">(YTD)</span>
            </CardTitle>
            <CardAction>
              <Select options={['This Year', 'Last Year']} aria-label="Reporting period" />
            </CardAction>
          </CardHeader>

          <CardContent className="flex-1">
            <dl className="mb-4 flex flex-wrap items-start gap-x-8 gap-y-3">
              <Stat label="YTD Spend" value={formatMoneyCompact(procurementTotals.ytdSpend, 1)} tone="text-primary" />
              <Stat label="Budget" value={formatMoneyCompact(procurementTotals.budget, 1)} tone="text-heading" />
              <Stat
                label="Variance"
                value={`-${formatMoneyCompact(Math.abs(procurementTotals.variance), 1)}`}
                tone="text-danger"
                note={formatPercent(procurementTotals.variancePercent)}
              />
            </dl>

            <SpendChart
              data={procurementSpend}
              ytdLabel={formatMoneyCompact(procurementTotals.ytdSpend, 1)}
              budgetLabel={formatMoneyCompact(procurementTotals.budget, 1)}
            />
          </CardContent>

          <FooterLink to="/procurement" label="View procurement dashboard" />
        </Card>

        <Card className="lg:col-span-2 2xl:col-span-1">
          <CardHeader>
            <CardTitle>Quick Actions</CardTitle>
          </CardHeader>
          <CardContent>
            <QuickActions actions={quickActions} />
          </CardContent>
        </Card>
      </section>

      <p className="text-muted flex items-center justify-end gap-2 text-xs">
        <span>
          Data as of <time dateTime={dataAsOf.toISOString()}>{formatDateTime(dataAsOf)}</time>
        </span>
        <RefreshCw className="size-3.5" strokeWidth={1.75} aria-hidden="true" />
      </p>
    </div>
  );
}

function Stat({
  label,
  value,
  tone,
  note,
}: {
  label: string;
  value: string;
  tone: string;
  note?: string;
}) {
  return (
    <div>
      <dt className="text-muted text-[11px] font-medium">{label}</dt>
      <dd className="mt-1 flex items-baseline gap-2">
        <span className={cn('tabular text-xl font-bold', tone)}>{value}</span>
        {note && <span className="text-muted tabular text-xs">{note}</span>}
      </dd>
    </div>
  );
}

/** The "View all …" affordance at the foot of a card. */
function FooterLink({ to, label }: { to: string; label: string }) {
  return (
    <div className="px-6 pb-5">
      <Link to={to} className="text-primary inline-flex items-center gap-2 text-[13px] font-medium hover:underline">
        {label}
        <ArrowRight className="size-4" strokeWidth={1.75} aria-hidden="true" />
      </Link>
    </div>
  );
}
