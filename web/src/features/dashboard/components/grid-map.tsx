/**
 * A stylised service-territory network — nodes coloured by the same semantic tones as the legend.
 * Static by design: the real map is WP-4.3's, and GIS is explicitly deferred in ARCHITECTURE.md.
 */
const nodes = [
  { x: 30, y: 46, tone: 'online' },
  { x: 66, y: 26, tone: 'online' },
  { x: 104, y: 40, tone: 'online' },
  { x: 142, y: 22, tone: 'online' },
  { x: 178, y: 36, tone: 'online' },
  { x: 16, y: 78, tone: 'online' },
  { x: 54, y: 70, tone: 'online' },
  { x: 96, y: 66, tone: 'warning' },
  { x: 134, y: 58, tone: 'online' },
  { x: 176, y: 74, tone: 'online' },
  { x: 34, y: 108, tone: 'online' },
  { x: 74, y: 100, tone: 'online' },
  { x: 112, y: 96, tone: 'online' },
  { x: 150, y: 92, tone: 'online' },
  { x: 62, y: 128, tone: 'online' },
  { x: 118, y: 124, tone: 'online' },
] as const;

const edges: [number, number][] = [
  [0, 1], [1, 2], [2, 3], [3, 4], [0, 5], [0, 6], [1, 6], [2, 7], [3, 8], [4, 9],
  [5, 6], [6, 7], [7, 8], [8, 9], [5, 10], [6, 11], [7, 12], [8, 13], [9, 13],
  [10, 11], [11, 12], [12, 13], [10, 14], [11, 14], [12, 15], [14, 15],
];

const toneColor: Record<string, string> = {
  online: 'var(--success)',
  warning: 'var(--warning)',
  outage: 'var(--danger)',
};

export function GridMap({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 200 150" className={className} role="img" aria-label="Service territory network status">
      {edges.map(([from, to]) => {
        const a = nodes[from]!;
        const b = nodes[to]!;
        return (
          <line
            key={`${from}-${to}`}
            x1={a.x}
            y1={a.y}
            x2={b.x}
            y2={b.y}
            stroke="var(--border)"
            strokeWidth="1"
          />
        );
      })}
      {nodes.map((node) => (
        <circle
          key={`${node.x}-${node.y}`}
          cx={node.x}
          cy={node.y}
          r="4.5"
          fill="var(--card)"
          stroke={toneColor[node.tone]}
          strokeWidth="1.75"
        />
      ))}
    </svg>
  );
}
