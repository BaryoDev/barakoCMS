'use client';

import Link from 'next/link';
import { useSchemas } from '@/hooks/use-schemas';
import { useContents } from '@/hooks/use-contents';
import { useWorkflows } from '@/hooks/use-workflows';
import { useHealthStatus, useMetrics } from '@/hooks/use-monitoring';
import { PageHeader } from '@/components/patterns/page-header';
import { StatusBadge, type Tone } from '@/components/patterns/status-badge';
import { Card, CardAction, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { Separator } from '@/components/ui/separator';
import {
  IconArrowRight,
  IconContent,
  IconContentTypes,
  IconPlus,
  IconWorkflows,
} from '@/components/icons';
import { formatDistanceToNow } from 'date-fns';

function healthTone(status?: string): Tone {
  if (status === 'Healthy') return 'success';
  if (status === 'Degraded') return 'warning';
  if (!status) return 'muted';
  return 'destructive';
}

function StatCard({
  label,
  value,
  href,
  isLoading,
}: {
  label: string;
  value: React.ReactNode;
  href: string;
  isLoading?: boolean;
}) {
  return (
    <Link href={href} className="group">
      <Card className="gap-2 py-5 transition-colors group-hover:border-ring/40">
        <CardHeader>
          <CardTitle className="text-muted-foreground text-sm font-medium">{label}</CardTitle>
          <CardAction>
            <IconArrowRight className="text-muted-foreground size-3.5 opacity-0 transition-opacity group-hover:opacity-100" />
          </CardAction>
        </CardHeader>
        <CardContent>
          {isLoading ? <Skeleton className="h-8 w-14" /> : <div className="text-2xl font-semibold">{value}</div>}
        </CardContent>
      </Card>
    </Link>
  );
}

export default function DashboardPage() {
  const { data: schemas, isLoading: schemasLoading } = useSchemas();
  const { data: contents, isLoading: contentsLoading } = useContents({ pageSize: 5 });
  const { data: workflows, isLoading: workflowsLoading } = useWorkflows();
  const { data: health } = useHealthStatus();
  const { data: metrics } = useMetrics();

  return (
    <>
      <PageHeader
        title="Overview"
        description="What's happening across your content, workflows, and system."
        actions={
          <Button asChild size="sm">
            <Link href="/content/new">
              <IconPlus />
              New entry
            </Link>
          </Button>
        }
      />

      <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
        <StatCard
          label="Content types"
          value={schemas?.length ?? 0}
          href="/schemas"
          isLoading={schemasLoading}
        />
        <StatCard
          label="Entries"
          value={contents?.totalItems ?? 0}
          href="/content"
          isLoading={contentsLoading}
        />
        <StatCard
          label="Workflows"
          value={workflows?.length ?? 0}
          href="/workflows"
          isLoading={workflowsLoading}
        />
      </div>

      <div className="mt-6 grid grid-cols-1 gap-4 lg:grid-cols-5">
        <Card className="lg:col-span-3">
          <CardHeader>
            <CardTitle className="text-sm font-medium">Latest entries</CardTitle>
            <CardAction>
              <Button asChild variant="ghost" size="sm" className="text-muted-foreground -my-1">
                <Link href="/content">
                  View all
                  <IconArrowRight className="size-3.5" />
                </Link>
              </Button>
            </CardAction>
          </CardHeader>
          <CardContent>
            {contentsLoading ? (
              <div className="space-y-2">
                {Array.from({ length: 4 }).map((_, i) => (
                  <Skeleton key={i} className="h-10 w-full" />
                ))}
              </div>
            ) : !contents?.items.length ? (
              <div className="text-muted-foreground py-8 text-center text-sm">
                No entries yet. Create a content type, then add your first entry.
              </div>
            ) : (
              <ul className="divide-y">
                {contents.items.map((item) => (
                  <li key={item.id}>
                    <Link
                      href={`/content/${item.id}`}
                      className="hover:bg-accent -mx-2 flex items-center gap-3 rounded-md px-2 py-2.5 transition-colors"
                    >
                      <IconContent className="text-muted-foreground size-4 shrink-0" />
                      <span className="min-w-0 flex-1 truncate text-sm font-medium">
                        {String(Object.values(item.data)[0] ?? item.id)}
                      </span>
                      <span className="text-muted-foreground shrink-0 font-mono text-xs">{item.contentType}</span>
                      <span className="text-muted-foreground hidden shrink-0 text-xs sm:block">
                        {formatDistanceToNow(new Date(item.updatedAt), { addSuffix: true })}
                      </span>
                    </Link>
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>

        <Card className="lg:col-span-2">
          <CardHeader>
            <CardTitle className="text-sm font-medium">System</CardTitle>
            <CardAction>
              <StatusBadge tone={healthTone(health?.status)}>{health?.status ?? 'Unknown'}</StatusBadge>
            </CardAction>
          </CardHeader>
          <CardContent className="space-y-3">
            {health?.entries &&
              Object.entries(health.entries).map(([name, entry]) => (
                <div key={name} className="flex items-center justify-between text-sm">
                  <span className="text-muted-foreground">{name}</span>
                  <StatusBadge tone={healthTone(entry.status)}>{entry.status}</StatusBadge>
                </div>
              ))}
            <Separator />
            <div className="flex items-center justify-between text-sm">
              <span className="text-muted-foreground">Requests served</span>
              <span className="font-mono text-xs">{metrics?.totalRequests?.toLocaleString() ?? '—'}</span>
            </div>
            <div className="flex items-center justify-between text-sm">
              <span className="text-muted-foreground">Error rate</span>
              <span className="font-mono text-xs">
                {metrics ? `${metrics.errorRate.toFixed(2)}%` : '—'}
              </span>
            </div>
            <Button asChild variant="outline" size="sm" className="w-full">
              <Link href="/ops/health">Full health report</Link>
            </Button>
          </CardContent>
        </Card>
      </div>

      <div className="mt-6 grid grid-cols-1 gap-4 sm:grid-cols-3">
        {[
          {
            href: '/schemas/new',
            icon: IconContentTypes,
            title: 'Define a content type',
            description: 'Model the shape of your content with typed fields.',
          },
          {
            href: '/content/new',
            icon: IconContent,
            title: 'Write an entry',
            description: 'Draft, publish, and track every version.',
          },
          {
            href: '/workflows/new',
            icon: IconWorkflows,
            title: 'Automate with a workflow',
            description: 'Send email, call webhooks, or update fields on content events.',
          },
        ].map((action) => (
          <Link key={action.href} href={action.href} className="group">
            <Card className="h-full gap-2 py-5 transition-colors group-hover:border-ring/40">
              <CardHeader>
                <CardTitle className="flex items-center gap-2.5 text-sm font-medium">
                  <action.icon className="text-primary size-4.5 shrink-0" />
                  {action.title}
                </CardTitle>
              </CardHeader>
              <CardContent>
                <p className="text-muted-foreground text-sm">{action.description}</p>
              </CardContent>
            </Card>
          </Link>
        ))}
      </div>
    </>
  );
}
