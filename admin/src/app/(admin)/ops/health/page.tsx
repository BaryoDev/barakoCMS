'use client';

import {
  useHealthStatus,
  useKubernetesStatus,
  useMetrics,
} from '@/hooks/use-monitoring';
import { PageHeader } from '@/components/patterns/page-header';
import { StatusBadge, type Tone } from '@/components/patterns/status-badge';
import { Card, CardAction, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { IconDatabase, IconDisk, IconHealth, IconMemory, IconServer } from '@/components/icons';

function healthTone(status?: string): Tone {
  if (status === 'Healthy' || status === 'True' || status === 'Ready') return 'success';
  if (status === 'Degraded') return 'warning';
  if (!status) return 'muted';
  return 'destructive';
}

const ENTRY_ICONS: Record<string, React.ComponentType<React.SVGProps<SVGSVGElement>>> = {
  Database: IconDatabase,
  Disk: IconDisk,
  Memory: IconMemory,
};

export default function HealthPage() {
  const { data: health, isLoading: healthLoading } = useHealthStatus();
  const { data: k8s } = useKubernetesStatus();
  const { data: metrics } = useMetrics();

  return (
    <>
      <PageHeader
        title="Health"
        description="Live checks refresh automatically — health every 15 seconds, metrics every 10."
        actions={
          health && <StatusBadge tone={healthTone(health.status)}>{health.status}</StatusBadge>
        }
      />

      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {healthLoading &&
          Array.from({ length: 3 }).map((_, i) => <Skeleton key={i} className="h-28 w-full" />)}
        {health &&
          Object.entries(health.entries).map(([name, entry]) => {
            const Icon = ENTRY_ICONS[name] ?? IconHealth;
            return (
              <Card key={name} className="gap-3 py-5">
                <CardHeader>
                  <CardTitle className="flex items-center gap-2 text-sm font-medium">
                    <Icon className="text-primary size-4" />
                    {name}
                  </CardTitle>
                  <CardAction>
                    <StatusBadge tone={healthTone(entry.status)}>{entry.status}</StatusBadge>
                  </CardAction>
                </CardHeader>
                <CardContent className="text-muted-foreground text-xs">
                  {entry.description || `Checked in ${entry.duration}`}
                </CardContent>
              </Card>
            );
          })}
      </div>

      <div className="mt-6 grid grid-cols-1 gap-4 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle className="text-sm font-medium">API traffic</CardTitle>
          </CardHeader>
          <CardContent className="grid grid-cols-2 gap-4">
            <Metric label="Requests" value={metrics?.totalRequests?.toLocaleString()} />
            <Metric label="Errors" value={metrics?.totalErrors?.toLocaleString()} />
            <Metric
              label="Avg response"
              value={metrics ? `${metrics.averageResponseTime.toFixed(0)} ms` : undefined}
            />
            <Metric label="Error rate" value={metrics ? `${metrics.errorRate.toFixed(2)}%` : undefined} />
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2 text-sm font-medium">
              <IconServer className="text-primary size-4" />
              Kubernetes
            </CardTitle>
            <CardAction>
              <StatusBadge tone={k8s?.isConnected ? 'success' : 'muted'}>
                {k8s?.isConnected ? 'Connected' : 'Not connected'}
              </StatusBadge>
            </CardAction>
          </CardHeader>
          <CardContent className="space-y-3 text-sm">
            {!k8s?.isConnected ? (
              <p className="text-muted-foreground text-xs">
                {k8s?.error || 'The API is not running inside a cluster, or the integration is turned off in Settings.'}
              </p>
            ) : (
              <>
                {k8s.nodes?.map((node) => (
                  <div key={node.name} className="flex items-center justify-between">
                    <span className="font-mono text-xs">{node.name}</span>
                    <StatusBadge tone={healthTone(node.status)}>{node.status}</StatusBadge>
                  </div>
                ))}
                {k8s.deployments?.map((deployment) => (
                  <div key={deployment.name} className="text-muted-foreground flex items-center justify-between text-xs">
                    <span className="font-mono">{deployment.namespace}/{deployment.name}</span>
                    <span>
                      {deployment.availableReplicas}/{deployment.replicas} replicas
                    </span>
                  </div>
                ))}
              </>
            )}
          </CardContent>
        </Card>
      </div>
    </>
  );
}

function Metric({ label, value }: { label: string; value?: string }) {
  return (
    <div>
      <p className="text-muted-foreground text-xs">{label}</p>
      <p className="mt-0.5 font-mono text-lg font-medium">{value ?? '—'}</p>
    </div>
  );
}
