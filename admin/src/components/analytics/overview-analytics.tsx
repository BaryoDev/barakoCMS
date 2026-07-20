'use client';

import Link from 'next/link';
import { Card, CardAction, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { Sparkline } from '@/components/analytics/sparkline';
import { IconArrowRight } from '@/components/icons';
import { useAnalyticsWebsites, useAnalyticsSummary, useAnalyticsSeries } from '@/hooks/use-analytics';

const nf = new Intl.NumberFormat();

/**
 * A compact visitors panel for the dashboard: last-7-day headline numbers plus a pageviews trend.
 * Self-gating — renders nothing when the Umami module isn't installed or tracks no sites, so the
 * dashboard stays clean on deployments (or tenants) without analytics.
 */
export function OverviewAnalytics() {
  const websites = useAnalyticsWebsites();
  const site = websites.data?.websites?.[0];
  const summary = useAnalyticsSummary(site?.id, '7d');
  const series = useAnalyticsSeries(site?.id, '7d');

  if (websites.isError) return null;
  if (websites.data && !websites.data.configured) return null;
  if (websites.data && !websites.data.websites?.length) return null;

  const visits = summary.data?.visits.value ?? 0;
  const bounceRate = visits > 0 ? Math.round(((summary.data?.bounces.value ?? 0) / visits) * 100) : 0;

  return (
    <Card className="mt-6">
      <CardHeader>
        <CardTitle className="text-sm font-medium">
          Visitors
          {site ? <span className="text-muted-foreground font-normal"> · {site.name}</span> : null}
          <span className="text-muted-foreground font-normal"> · last 7 days</span>
        </CardTitle>
        <CardAction>
          <Button asChild variant="ghost" size="sm" className="text-muted-foreground -my-1">
            <Link href="/analytics">
              Analytics
              <IconArrowRight className="size-3.5" />
            </Link>
          </Button>
        </CardAction>
      </CardHeader>
      <CardContent className="space-y-4">
        <div className="grid grid-cols-2 gap-4 sm:grid-cols-4">
          <Metric label="Visitors" value={summary.data?.visitors.value} loading={summary.isLoading} />
          <Metric label="Pageviews" value={summary.data?.pageviews.value} loading={summary.isLoading} />
          <Metric label="Visits" value={visits} loading={summary.isLoading} />
          <Metric label="Bounce rate" text={`${bounceRate}%`} loading={summary.isLoading} />
        </div>
        {series.isLoading ? (
          <Skeleton className="h-16 w-full" />
        ) : series.data && series.data.pageviews.length > 0 ? (
          <Sparkline values={series.data.pageviews.map((p) => p.y)} height={72} />
        ) : (
          <p className="text-muted-foreground text-sm">No visits in the last 7 days.</p>
        )}
      </CardContent>
    </Card>
  );
}

function Metric({
  label,
  value,
  text,
  loading,
}: {
  label: string;
  value?: number;
  text?: string;
  loading?: boolean;
}) {
  return (
    <div>
      <p className="text-muted-foreground text-xs">{label}</p>
      {loading ? (
        <Skeleton className="mt-1 h-6 w-12" />
      ) : (
        <p className="mt-0.5 font-mono text-xl font-semibold">{text ?? nf.format(value ?? 0)}</p>
      )}
    </div>
  );
}
