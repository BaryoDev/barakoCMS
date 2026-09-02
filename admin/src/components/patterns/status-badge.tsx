import { Badge } from '@/components/ui/badge';
import { cn } from '@/lib/utils';

export type Tone = 'success' | 'warning' | 'muted' | 'destructive' | 'accent';

/**
 * The Signal tint pairs: a solid soft background with its measured ink on top, no border.
 *
 * These are the pairs from the design token table, and each was measured against the tint it sits
 * on rather than against white: 4.73:1 success, 5.35 warning, 6.27 danger, 7.89 accent, 7.37 muted.
 *
 * The previous set built the background with an alpha (`bg-warning/10`) and took the foreground from
 * `--warning-foreground`, which is white, because that token exists for white-on-solid buttons. A
 * warning badge was therefore white text on a 10%-opacity wash of white, and nothing caught it: the
 * axe case for the content list stubs an empty page, so no badge ever rendered under the gate.
 */
const TONE_CLASSES: Record<Tone, string> = {
  success: 'border-transparent bg-[var(--success-soft)] text-success',
  warning: 'border-transparent bg-[var(--warning-soft)] text-warning',
  muted: 'border-transparent bg-secondary text-secondary-foreground',
  destructive: 'border-transparent bg-[var(--danger-soft)] text-destructive',
  accent: 'border-transparent bg-accent text-accent-foreground',
};

interface StatusBadgeProps {
  tone: Tone;
  children: React.ReactNode;
  className?: string;
  /** Show a small dot before the label */
  dot?: boolean;
}

export function StatusBadge({ tone, children, className, dot = true }: StatusBadgeProps) {
  return (
    <Badge
      variant="outline"
      className={cn('gap-1.5 px-2.5 py-[3px] text-[11px] font-bold', TONE_CLASSES[tone], className)}
    >
      {dot && <span className="size-1.5 rounded-full bg-current" aria-hidden />}
      {children}
    </Badge>
  );
}
