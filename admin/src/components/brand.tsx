'use client';

import { useId } from 'react';

import { cn } from '@/lib/utils';

/**
 * The mark, wherever the product signs its name: the sidebar and the admin shell.
 *
 * It was a mug glyph reversed out of a filled purple tile. The bean is the Signal mark and the
 * sign-in page has been drawing it since that design landed, so the two front doors of the same
 * product did not look like the same product. This is the same component the sign-in page uses,
 * at the same footprint the tile had, so no call site changes.
 */
export function BrandMark({ className }: { className?: string }) {
  return <BrandBean className={cn('size-8 shrink-0', className)} />;
}

export function BrandWordmark({ className }: { className?: string }) {
  return (
    <span className={cn('font-display text-lg font-semibold tracking-tight', className)}>
      Barako
      <span className="text-muted-foreground font-sans text-sm font-medium align-baseline ml-0.5">CMS</span>
    </span>
  );
}

/**
 * The coffee bean, the Signal mark.
 *
 * The gradient id is scoped with useId because the sign-in page draws the bean twice, once as the
 * 44px mark and once as the oversized watermark, and two <defs> sharing a literal id means the
 * second one silently wins for both.
 */
export function BrandBean({
  className,
  decorative = false,
  title = 'BarakoCMS',
}: {
  className?: string;
  /** Drop the highlight and the accessible name. For the watermark, which carries no meaning. */
  decorative?: boolean;
  title?: string;
}) {
  const gradientId = `bean-${useId()}`;

  return (
    <svg
      viewBox="0 0 128 128"
      className={className}
      role={decorative ? undefined : 'img'}
      aria-label={decorative ? undefined : title}
      aria-hidden={decorative || undefined}
    >
      <defs>
        <linearGradient id={gradientId} x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor="#9c8df5" />
          <stop offset="0.55" stopColor="var(--primary)" />
          <stop offset="1" stopColor="#33257f" />
        </linearGradient>
      </defs>
      <g transform="rotate(-32 64 64)">
        <ellipse cx="64" cy="64" rx="33" ry="50" fill={`url(#${gradientId})`} />
        {!decorative && <ellipse cx="52" cy="44" rx="9" ry="17" fill="#c9c1f5" opacity="0.35" />}
        <path
          d="M64 17 C 51 41, 77 55, 64 64 C 51 73, 77 87, 64 111"
          fill="none"
          stroke="var(--accent-deep)"
          strokeWidth="6.5"
          strokeLinecap="round"
        />
      </g>
    </svg>
  );
}
