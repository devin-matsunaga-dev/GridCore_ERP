import { Cell, Pie, PieChart, ResponsiveContainer } from 'recharts';
import { StatusDot, toneFor } from '@/components/ui/status';
import { formatCount, formatPercent } from '@/lib/format';
import type { WorkOrderSlice } from '../demo-data';

/** DESIGN.md donut: centre total + label, legend right with counts and percentages. */
export function WorkOrderDonut({ slices }: { slices: WorkOrderSlice[] }) {
  const total = slices.reduce((sum, slice) => sum + slice.count, 0);

  return (
    <div className="flex flex-wrap items-center gap-x-6 gap-y-5">
      <div className="relative size-40 shrink-0">
        <ResponsiveContainer width="100%" height="100%">
          <PieChart>
            <Pie
              data={slices}
              dataKey="count"
              nameKey="status"
              innerRadius="72%"
              outerRadius="100%"
              paddingAngle={1.5}
              startAngle={90}
              endAngle={-270}
              stroke="none"
              isAnimationActive={false}
            >
              {slices.map((slice) => (
                <Cell key={slice.status} fill={slice.color} />
              ))}
            </Pie>
          </PieChart>
        </ResponsiveContainer>

        <div className="pointer-events-none absolute inset-0 flex flex-col items-center justify-center">
          <span className="text-heading tabular text-2xl font-bold">{formatCount(total)}</span>
          <span className="text-muted text-[13px]">Total</span>
        </div>
      </div>

      <ul className="flex-1 basis-36 space-y-3">
        {slices.map((slice) => (
          <li key={slice.status} className="flex items-center justify-between gap-3 text-sm">
            <StatusDot tone={toneFor(slice.status)} label={slice.status} className="text-body whitespace-nowrap" />
            <span className="text-heading tabular shrink-0 font-medium">
              {formatCount(slice.count)}{' '}
              <span className="text-muted font-normal">({formatPercent(slice.count / total, 0)})</span>
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}
