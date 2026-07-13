import type { ComponentType, SVGProps } from 'react';
import { cn } from '@/lib/utils';

interface EmptyStateProps {
  icon: ComponentType<SVGProps<SVGSVGElement>>;
  title: string;
  description: string;
  action?: React.ReactNode;
  className?: string;
}

export function EmptyState({ icon: Icon, title, description, action, className }: EmptyStateProps) {
  return (
    <div
      className={cn(
        'flex flex-col items-center justify-center rounded-lg border border-dashed px-6 py-16 text-center',
        className
      )}
    >
      <div className="bg-muted text-muted-foreground mb-4 flex size-11 items-center justify-center rounded-lg">
        <Icon className="size-5" />
      </div>
      <h3 className="text-sm font-medium">{title}</h3>
      <p className="text-muted-foreground mt-1 max-w-sm text-sm">{description}</p>
      {action && <div className="mt-4">{action}</div>}
    </div>
  );
}
