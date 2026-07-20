'use client';

import { useState } from 'react';
import { PageHeader } from '@/components/patterns/page-header';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { AddWebsiteDialog } from '@/components/analytics/add-website-dialog';
import { Sparkline } from '@/components/analytics/sparkline';
import { IconAnalytics } from '@/components/icons';
import {
  useAnalyticsWebsites,
  useAnalyticsSummary,
  useAnalyticsSeries,
  useAnalyticsMetric,
  type AnalyticsRange,
  type MetricType,
  type StatValue,
} from '@/hooks/use-analytics';

const RANGES: { value: AnalyticsRange; label: string }[] = [
  { value: '24h', label: 'Last 24 hours' },
  { value: '7d', label: 'Last 7 days' },
  { value: '30d', label: 'Last 30 days' },
  { value: '90d', label: 'Last 90 days' },
];

const nf = new Intl.NumberFormat();

function formatDuration(seconds: number): string {
  if (!seconds || seconds < 1) return '0s';
  const m = Math.floor(seconds / 60);
  const s = Math.round(seconds % 60);
  return m > 0 ? `${m}m ${s}s` : `${s}s`;
}

/** Regional-indicator flag for a 2-letter ISO country code (best-effort; blank for unknown). */
function flag(code: string): string {
  if (!/^[a-zA-Z]{2}$/.test(code)) return '';
  return String.fromCodePoint(...[...code.toUpperCase()].map((c) => 0x1f1e6 + c.charCodeAt(0) - 65));
}

export default function AnalyticsPage() {
  const websites = useAnalyticsWebsites();
  const [selectedId, setSelectedId] = useState<string>();
  const [range, setRange] = useState<AnalyticsRange>('7d');

  // Derive the active site: the user's pick, else the first tracked one — no effect needed.
  const websiteId = selectedId ?? websites.data?.websites?.[0]?.id;
  const setWebsiteId = setSelectedId;

  const notConfigured = websites.data && !websites.data.configured;
  const hasSites = !!websites.data?.websites?.length;
  const current = websites.data?.websites?.find((w) => w.id === websiteId);

  return (
    <>
      <PageHeader
        title="Analytics"
        description="Visitor traffic from Umami — who's visiting, what they read, and where they come from."
        actions={
          hasSites && (
            <div className="flex items-center gap-2">
              <Select value={range} onValueChange={(v) => setRange(v as AnalyticsRange)}>
                <SelectTrigger className="w-40" size="sm">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {RANGES.map((r) => (
                    <SelectItem key={r.value} value={r.value}>
                      {r.label}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <AddWebsiteDialog onCreated={setWebsiteId} />
            </div>
          )
        }
      />

      {notConfigured && <NotConfigured />}

      {!notConfigured && !hasSites && !websites.isLoading && <NoSites />}

      {websites.isLoading && <Skeleton className="h-40 w-full" />}

      {hasSites && (
        <>
          {websites.data!.websites.length > 1 && (
            <div className="mb-4">
              <Select value={websiteId} onValueChange={setWebsiteId}>
                <SelectTrigger className="w-64">
                  <SelectValue placeholder="Choose a website" />
                </SelectTrigger>
                <SelectContent>
                  {websites.data!.websites.map((w) => (
                    <SelectItem key={w.id} value={w.id}>
                      {w.name} · {w.domain}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
          )}

          <Summary websiteId={websiteId} range={range} />
          <Trend websiteId={websiteId} range={range} />

          <div className="mt-6 grid grid-cols-1 gap-4 lg:grid-cols-3">
            <MetricCard title="Top pages" websiteId={websiteId} range={range} type="url" mono />
            <MetricCard title="Referrers" websiteId={websiteId} range={range} type="referrer" empty="Direct visits only" />
            <MetricCard title="Countries" websiteId={websiteId} range={range} type="country" country />
          </div>

          {current && (
            <p className="text-muted-foreground mt-6 text-xs">
              Showing {current.name} ({current.domain}).
            </p>
          )}
        </>
      )}
    </>
  );
}

function delta(s?: StatValue): { pct: number; up: boolean } | null {
  if (!s || !s.previous) return null;
  const pct = ((s.value - s.previous) / s.previous) * 100;
  if (!isFinite(pct) || Math.abs(pct) < 0.5) return null;
  return { pct: Math.round(pct), up: pct >= 0 };
}

function StatCard({ label, value, s }: { label: string; value: string; s?: StatValue }) {
  const d = delta(s);
  return (
    <Card className="gap-2 py-5">
      <CardHeader>
        <CardTitle className="text-muted-foreground text-xs font-medium">{label}</CardTitle>
      </CardHeader>
      <CardContent>
        <p className="font-mono text-2xl font-semibold">{value}</p>
        {d && (
          <p className={`mt-1 text-xs ${d.up ? 'text-emerald-600' : 'text-rose-600'}`}>
            {d.up ? '▲' : '▼'} {Math.abs(d.pct)}% vs previous
          </p>
        )}
      </CardContent>
    </Card>
  );
}

function Summary({ websiteId, range }: { websiteId?: string; range: AnalyticsRange }) {
  const { data, isLoading } = useAnalyticsSummary(websiteId, range);
  if (isLoading) {
    return (
      <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
        {Array.from({ length: 4 }).map((_, i) => (
          <Skeleton key={i} className="h-28 w-full" />
        ))}
      </div>
    );
  }
  const visits = data?.visits.value ?? 0;
  const bounceRate = visits > 0 ? Math.round(((data?.bounces.value ?? 0) / visits) * 100) : 0;
  const avgTime = visits > 0 ? (data?.totalTime.value ?? 0) / visits : 0;
  return (
    <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
      <StatCard label="Visitors" value={nf.format(data?.visitors.value ?? 0)} s={data?.visitors} />
      <StatCard label="Pageviews" value={nf.format(data?.pageviews.value ?? 0)} s={data?.pageviews} />
      <StatCard label="Avg. visit" value={formatDuration(avgTime)} />
      <StatCard label="Bounce rate" value={`${bounceRate}%`} />
    </div>
  );
}

function Trend({ websiteId, range }: { websiteId?: string; range: AnalyticsRange }) {
  const { data, isLoading } = useAnalyticsSeries(websiteId, range);
  return (
    <Card className="mt-6">
      <CardHeader>
        <CardTitle className="text-sm font-medium">Pageviews over time</CardTitle>
      </CardHeader>
      <CardContent>
        {isLoading ? (
          <Skeleton className="h-14 w-full" />
        ) : data && data.pageviews.length > 0 ? (
          <Sparkline values={data.pageviews.map((p) => p.y)} height={64} />
        ) : (
          <p className="text-muted-foreground text-sm">No visits in this period yet.</p>
        )}
      </CardContent>
    </Card>
  );
}

function MetricCard({
  title,
  websiteId,
  range,
  type,
  mono,
  country,
  empty = 'No data yet',
}: {
  title: string;
  websiteId?: string;
  range: AnalyticsRange;
  type: MetricType;
  mono?: boolean;
  country?: boolean;
  empty?: string;
}) {
  const { data, isLoading } = useAnalyticsMetric(websiteId, type, range, 8);
  const max = Math.max(1, ...(data?.map((r) => r.y) ?? [1]));
  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-sm font-medium">{title}</CardTitle>
      </CardHeader>
      <CardContent className="space-y-1.5">
        {isLoading ? (
          Array.from({ length: 5 }).map((_, i) => <Skeleton key={i} className="h-6 w-full" />)
        ) : data && data.length > 0 ? (
          data.map((row) => {
            const label = row.x || (type === 'referrer' ? 'Direct' : '(none)');
            return (
              <div key={label} className="relative flex items-center justify-between gap-2 rounded px-2 py-1 text-sm">
                {/* Proportional bar behind the row, like Umami's own breakdowns. */}
                <span
                  className="bg-primary/10 absolute inset-y-0 left-0 rounded"
                  style={{ width: `${(row.y / max) * 100}%` }}
                  aria-hidden
                />
                <span className={`relative z-10 min-w-0 truncate ${mono ? 'font-mono text-xs' : ''}`}>
                  {country && flag(row.x) ? <span className="mr-1.5">{flag(row.x)}</span> : null}
                  {label}
                </span>
                <span className="relative z-10 font-mono text-xs tabular-nums">{nf.format(row.y)}</span>
              </div>
            );
          })
        ) : (
          <p className="text-muted-foreground py-2 text-sm">{empty}</p>
        )}
      </CardContent>
    </Card>
  );
}

function NotConfigured() {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-sm font-medium">
          <IconAnalytics className="text-primary size-4" />
          Connect Umami
        </CardTitle>
      </CardHeader>
      <CardContent className="text-muted-foreground space-y-2 text-sm">
        <p>
          The analytics module is installed but not connected. Set these on the API host and restart:
        </p>
        <pre className="bg-muted overflow-x-auto rounded-md p-3 text-xs">
{`Umami__Enabled=true
Umami__BaseUrl=http://umami:3000
Umami__Username=admin
Umami__Password=•••••
Umami__PublicUrl=https://playground.baryo.dev/analytics`}
        </pre>
      </CardContent>
    </Card>
  );
}

function NoSites() {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-sm font-medium">No websites yet</CardTitle>
      </CardHeader>
      <CardContent className="text-muted-foreground flex flex-col items-start gap-3 text-sm">
        <p>Umami isn&apos;t tracking any sites. Add one to get a snippet and start collecting visits.</p>
        <AddWebsiteDialog />
      </CardContent>
    </Card>
  );
}
