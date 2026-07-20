'use client';

/** A tiny dependency-free area/line chart for a single series. Scales to its container width. */
export function Sparkline({
  values,
  labels,
  height = 56,
  className,
}: {
  values: number[];
  labels?: string[];
  height?: number;
  className?: string;
}) {
  const width = 600; // viewBox units; the SVG stretches to the container via preserveAspectRatio.
  if (values.length === 0) {
    return <div className={className} style={{ height }} />;
  }
  const max = Math.max(...values, 1);
  const n = values.length;
  const step = n > 1 ? width / (n - 1) : 0;
  const y = (v: number) => height - (v / max) * (height - 4) - 2;
  const pts = values.map((v, i) => `${(i * step).toFixed(2)},${y(v).toFixed(2)}`);
  const line = pts.map((p, i) => (i === 0 ? `M ${p}` : `L ${p}`)).join(' ');
  const area = `${line} L ${(width).toFixed(2)},${height} L 0,${height} Z`;

  return (
    <svg
      className={className}
      viewBox={`0 0 ${width} ${height}`}
      preserveAspectRatio="none"
      width="100%"
      height={height}
      role="img"
      aria-label={labels ? `Trend: ${values.join(', ')}` : 'Trend'}
    >
      <path d={area} className="fill-primary/10" />
      <path d={line} className="stroke-primary" fill="none" strokeWidth={2} vectorEffect="non-scaling-stroke" />
    </svg>
  );
}
