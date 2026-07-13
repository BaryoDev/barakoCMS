import { Badge } from '@/components/ui/badge';
import { cn } from '@/lib/utils';

export type Tone = 'success' | 'warning' | 'muted' | 'destructive';

const TONE_CLASSES: Record<Tone, string> = {
  success: 'border-success/40 bg-success/10 text-success',
  warning: 'border-warning/40 bg-warning/10 text-warning-foreground dark:text-warning',
  muted: 'border-border bg-muted text-muted-foreground',
  destructive: 'border-destructive/40 bg-destructive/10 text-destructive',
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
    <Badge variant="outline" className={cn('gap-1.5 font-normal', TONE_CLASSES[tone], className)}>
      {dot && <span className="size-1.5 rounded-full bg-current" aria-hidden />}
      {children}
    </Badge>
  );
}
