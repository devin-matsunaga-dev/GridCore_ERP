import { Bar, CartesianGrid, ComposedChart, Line, ResponsiveContainer, XAxis, YAxis } from 'recharts';
import { formatMoneyCompact, niceTicks } from '@/lib/format';
import type { SpendPoint } from '../demo-data';

/**
 * Monthly spend as bars, spend-to-date and budget-to-date as lines. DESIGN.md's chart rules hold:
 * 2px primary line, dashed gridlines, muted axis labels, categorical colours in order.
 */
export function SpendChart({
  data,
  ytdLabel,
  budgetLabel,
}: {
  data: SpendPoint[];
  ytdLabel: string;
  budgetLabel: string;
}) {
  const ticks = niceTicks(Math.max(...data.map((point) => Math.max(point.budget, point.ytd ?? 0))));
  const ceiling = ticks.at(-1)!;

  // Monthly spend is an order of magnitude below the running totals, so plotted against the same
  // axis the bars would be a flat smear along the baseline. They are drawn against a scale of
  // their own — the tallest bar reaches half the plot — which is why the bars carry no axis
  // labels: they show the shape of monthly spend, and the axis belongs to the two lines.
  const barScale = ceiling / 2 / Math.max(...data.map((point) => point.monthly));
  const plotted = data.map((point) => ({ ...point, monthlyPlotted: point.monthly * barScale }));

  return (
    <div className="space-y-2">
      <p className="text-muted text-[11px]">Spend (M)</p>

      <div className="h-52 w-full">
        <ResponsiveContainer width="100%" height="100%">
          <ComposedChart data={plotted} margin={{ top: 4, right: 8, bottom: 0, left: 0 }}>
            <CartesianGrid stroke="var(--border)" strokeDasharray="4 4" vertical={false} />
            <XAxis
              dataKey="month"
              tickLine={false}
              axisLine={false}
              tick={{ fill: 'var(--text-muted)', fontSize: 11 }}
              interval={0}
              dy={6}
            />
            <YAxis
              tickLine={false}
              axisLine={false}
              width={44}
              domain={[0, ceiling]}
              ticks={ticks}
              tick={{ fill: 'var(--text-muted)', fontSize: 11 }}
              tickFormatter={(value: number) => formatMoneyCompact(value)}
            />
            <Bar
              dataKey="monthlyPlotted"
              fill="var(--chart-5)"
              radius={[2, 2, 0, 0]}
              maxBarSize={18}
              isAnimationActive={false}
            />
            <Line
              type="linear"
              dataKey="budget"
              stroke="var(--text-muted)"
              strokeWidth={1.5}
              strokeDasharray="5 4"
              dot={false}
              isAnimationActive={false}
            />
            <Line
              type="linear"
              dataKey="ytd"
              stroke="var(--primary)"
              strokeWidth={2}
              dot={{ r: 3, fill: 'var(--card)', stroke: 'var(--primary)', strokeWidth: 2 }}
              activeDot={{ r: 5 }}
              connectNulls={false}
              isAnimationActive={false}
            />
          </ComposedChart>
        </ResponsiveContainer>
      </div>

      <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-2">
        <ul className="text-muted flex flex-wrap items-center gap-x-4 gap-y-1 text-[11px]">
          <li className="flex items-center gap-1.5">
            <span className="bg-primary size-2 rounded-full" aria-hidden="true" />
            YTD Spend
          </li>
          <li className="flex items-center gap-1.5">
            <span className="border-muted w-3.5 border-t-2 border-dashed" aria-hidden="true" />
            Budget
          </li>
          <li className="flex items-center gap-1.5">
            <span className="bg-chart-5 h-2.5 w-2 rounded-[1px]" aria-hidden="true" />
            Monthly Spend
          </li>
        </ul>

        <div className="flex items-center gap-2">
          <span className="bg-neutral-soft text-muted tabular rounded px-1.5 py-0.5 text-[11px] font-medium">
            {budgetLabel}
          </span>
          <span className="bg-primary-soft text-primary tabular rounded px-1.5 py-0.5 text-[11px] font-medium">
            {ytdLabel}
          </span>
        </div>
      </div>
    </div>
  );
}
