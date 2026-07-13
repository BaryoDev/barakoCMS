import { IconMug } from '@/components/icons';
import { cn } from '@/lib/utils';

export function BrandMark({ className }: { className?: string }) {
  return (
    <div
      className={cn(
        'flex size-8 shrink-0 items-center justify-center rounded-lg bg-primary text-primary-foreground',
        className
      )}
    >
      <IconMug className="size-4.5" />
    </div>
  );
}

export function BrandWordmark({ className }: { className?: string }) {
  return (
    <span className={cn('font-display text-lg font-semibold tracking-tight', className)}>
      Barako
      <span className="text-muted-foreground font-sans text-sm font-medium align-baseline ml-0.5">CMS</span>
    </span>
  );
}
