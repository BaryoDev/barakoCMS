'use client';

import { PageHeader } from '@/components/patterns/page-header';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { Skeleton } from '@/components/ui/skeleton';
import { Switch } from '@/components/ui/switch';
import {
  useFeatureFlags,
  useToggleFeatureFlag,
  type FeatureFlag,
} from '@/hooks/use-feature-flags';

export default function FeatureFlagsPage() {
  const { data, isLoading, isError } = useFeatureFlags();
  const toggle = useToggleFeatureFlag();

  return (
    <>
      <PageHeader
        title="Feature flags"
        description="Toggle module and app features. Changes take effect immediately."
      />

      {isError ? (
        <NotAvailable />
      ) : (
        <Card>
          <CardHeader>
            <CardTitle className="text-sm font-medium">Flags</CardTitle>
          </CardHeader>
          <CardContent className="space-y-1">
            {isLoading ? (
              Array.from({ length: 4 }).map((_, i) => (
                <div key={i} className="flex items-center gap-3 py-2">
                  <Skeleton className="h-[1.15rem] w-8 shrink-0 rounded-full" />
                  <div className="min-w-0 flex-1 space-y-1.5">
                    <Skeleton className="h-4 w-40" />
                    <Skeleton className="h-3 w-64" />
                  </div>
                </div>
              ))
            ) : !data || data.length === 0 ? (
              <p className="text-muted-foreground py-2 text-sm">No feature flags defined.</p>
            ) : (
              data.map((flag) => (
                <FlagRow
                  key={flag.key}
                  flag={flag}
                  pending={toggle.isPending && toggle.variables === flag.key}
                  onToggle={() => toggle.mutate(flag.key)}
                />
              ))
            )}
          </CardContent>
        </Card>
      )}
    </>
  );
}

function FlagRow({
  flag,
  pending,
  onToggle,
}: {
  flag: FeatureFlag;
  pending: boolean;
  onToggle: () => void;
}) {
  const scoped = flag.tenantSlugs.length > 0 || flag.userEmails.length > 0;
  return (
    <div className="flex items-start justify-between gap-3 border-b py-3 last:border-b-0">
      <div className="min-w-0">
        <div className="flex flex-wrap items-center gap-2">
          <span className="font-mono text-sm font-medium">{flag.key}</span>
          {flag.rolloutPercent < 100 && (
            <span className="text-muted-foreground text-xs">{flag.rolloutPercent}% rollout</span>
          )}
          {scoped && (
            <Badge variant="secondary" className="text-xs">
              scoped
            </Badge>
          )}
        </div>
        {flag.description && (
          <p className="text-muted-foreground mt-0.5 text-sm">{flag.description}</p>
        )}
      </div>
      <Switch
        checked={flag.enabled}
        disabled={pending}
        onCheckedChange={onToggle}
        aria-label={`Toggle ${flag.key}`}
        className="mt-0.5 shrink-0"
      />
    </div>
  );
}

function NotAvailable() {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-sm font-medium">Feature flags module not available</CardTitle>
      </CardHeader>
      <CardContent className="text-muted-foreground text-sm">
        <p>
          The feature flags module isn&apos;t installed on this API host, or the endpoint returned an
          error. Install the module and restart to manage flags here.
        </p>
      </CardContent>
    </Card>
  );
}
