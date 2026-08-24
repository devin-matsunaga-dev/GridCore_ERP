/**
 * A tiny trend line for a KPI card. Hand-drawn SVG rather than a chart library: it carries no
 * axes, ticks or tooltips, and a Recharts instance per KPI would cost far more than it returns.
 */
export function Sparkline({
  values,
  className,
  tone = 'var(--success)',
}: {
  values: readonly number[];
  className?: string;
  tone?: string;
}) {
  if (values.length < 2) return null;

  const width = 100;
  const height = 32;
  const padding = 2;

  const min = Math.min(...values);
  const max = Math.max(...values);
  // A flat series would divide by zero; draw it down the middle instead.
  const span = max - min || 1;

  const points = values.map((value, index) => {
    const x = padding + (index / (values.length - 1)) * (width - padding * 2);
    const y = height - padding - ((value - min) / span) * (height - padding * 2);
    return `${x.toFixed(2)},${y.toFixed(2)}`;
  });

  return (
    <svg
      viewBox={`0 0 ${width} ${height}`}
      preserveAspectRatio="none"
      className={className}
      role="presentation"
      aria-hidden="true"
    >
      <polyline
        points={points.join(' ')}
        fill="none"
        stroke={tone}
        strokeWidth="2"
        strokeLinecap="round"
        strokeLinejoin="round"
        vectorEffect="non-scaling-stroke"
      />
    </svg>
  );
}
