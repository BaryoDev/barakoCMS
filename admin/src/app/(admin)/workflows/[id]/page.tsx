'use client';

import { use, useState } from 'react';
import { toast } from 'sonner';
import {
  useDryRunWorkflow,
  useWorkflow,
  useWorkflowDebugLogs,
} from '@/hooks/use-workflows';
import { apiErrorMessage } from '@/lib/api';
import { PageHeader } from '@/components/patterns/page-header';
import { StatusBadge } from '@/components/patterns/status-badge';
import { TableSkeleton } from '@/components/patterns/table-skeleton';
import { EmptyState } from '@/components/patterns/empty-state';
import { ActionIcon } from '@/components/workflow/action-icon';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Textarea } from '@/components/ui/textarea';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { IconBolt, IconBug, IconPlay, IconWorkflows } from '@/components/icons';
import { format } from 'date-fns';
import Link from 'next/link';

export default function WorkflowDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params);
  const { data: workflow, isLoading } = useWorkflow(id);

  if (isLoading) return <TableSkeleton />;

  if (!workflow) {
    return (
      <EmptyState
        icon={IconWorkflows}
        title="Workflow not found"
        description="This workflow does not exist anymore, or the list has not caught up yet."
        action={
          <Button asChild variant="outline" size="sm">
            <Link href="/workflows">Back to workflows</Link>
          </Button>
        }
      />
    );
  }

  const conditionEntries = Object.entries(workflow.conditions ?? {});

  return (
    <>
      <PageHeader
        title={workflow.name}
        description={`Runs when a ${workflow.triggerContentType} entry is ${workflow.triggerEvent.toLowerCase()}.`}
      />

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2 text-sm font-medium">
              <IconBolt className="text-primary size-4" />
              Definition
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-4 text-sm">
            <div>
              <p className="text-muted-foreground text-xs font-medium uppercase tracking-wide">Trigger</p>
              <p className="mt-1">
                <span className="font-mono text-xs">{workflow.triggerContentType}</span> ·{' '}
                {workflow.triggerEvent}
              </p>
            </div>
            <div>
              <p className="text-muted-foreground text-xs font-medium uppercase tracking-wide">Conditions</p>
              {conditionEntries.length === 0 ? (
                <p className="text-muted-foreground mt-1">Always runs</p>
              ) : (
                <ul className="mt-1 space-y-1">
                  {conditionEntries.map(([field, value]) => (
                    <li key={field} className="font-mono text-xs">
                      {field} = {value}
                    </li>
                  ))}
                </ul>
              )}
            </div>
            <div>
              <p className="text-muted-foreground text-xs font-medium uppercase tracking-wide">Actions</p>
              <ol className="mt-2 space-y-2">
                {workflow.actions.map((action, i) => (
                  <li key={i} className="rounded-md border px-3 py-2">
                    <span className="flex items-center gap-2 font-medium">
                      <ActionIcon type={action.type} className="text-primary size-3.5" />
                      {i + 1}. {action.type}
                    </span>
                    {Object.entries(action.parameters).length > 0 && (
                      <dl className="text-muted-foreground mt-1.5 space-y-0.5 font-mono text-xs">
                        {Object.entries(action.parameters).map(([key, value]) => (
                          <div key={key} className="flex gap-2">
                            <dt className="shrink-0">{key}:</dt>
                            <dd className="truncate">{value}</dd>
                          </div>
                        ))}
                      </dl>
                    )}
                  </li>
                ))}
              </ol>
            </div>
          </CardContent>
        </Card>

        <Card>
          <Tabs defaultValue="runs">
            <CardHeader>
              <TabsList>
                <TabsTrigger value="runs">
                  <IconBug className="size-3.5" />
                  Recent runs
                </TabsTrigger>
                <TabsTrigger value="dry-run">
                  <IconPlay className="size-3.5" />
                  Dry run
                </TabsTrigger>
              </TabsList>
            </CardHeader>
            <CardContent>
              <TabsContent value="runs">
                <RunsPanel workflowId={id} />
              </TabsContent>
              <TabsContent value="dry-run">
                <DryRunPanel workflow={workflow} />
              </TabsContent>
            </CardContent>
          </Tabs>
        </Card>
      </div>
    </>
  );
}

function RunsPanel({ workflowId }: { workflowId: string }) {
  const { data: logs, isLoading } = useWorkflowDebugLogs(workflowId);

  if (isLoading) return <TableSkeleton rows={3} />;
  if (!logs?.length) {
    return (
      <p className="text-muted-foreground py-8 text-center text-sm">
        This workflow has not run yet. It executes when matching content events occur.
      </p>
    );
  }

  return (
    <ul className="space-y-2">
      {logs.map((log) => (
        <li key={log.id} className="rounded-md border px-3 py-2">
          <div className="flex items-center justify-between gap-2">
            <span className="text-sm">{format(new Date(log.executedAt), 'PPp')}</span>
            <StatusBadge tone={log.success ? 'success' : 'destructive'}>
              {log.success ? 'Succeeded' : 'Failed'}
            </StatusBadge>
          </div>
          <ul className="text-muted-foreground mt-1.5 space-y-0.5 text-xs">
            {log.actions.map((action, i) => (
              <li key={i}>
                {action.actionType}: {action.success ? 'ok' : action.errorMessage ?? 'failed'}
              </li>
            ))}
          </ul>
        </li>
      ))}
    </ul>
  );
}

function DryRunPanel({ workflow }: { workflow: import('@/types/workflow').WorkflowDefinition }) {
  const dryRun = useDryRunWorkflow();
  const [sample, setSample] = useState(() =>
    JSON.stringify({ contentType: workflow.triggerContentType, data: {} }, null, 2)
  );

  const run = () => {
    let sampleContent: unknown;
    try {
      sampleContent = JSON.parse(sample);
    } catch {
      toast.error('The sample content must be valid JSON.');
      return;
    }
    dryRun.mutate(
      { workflow, sampleContent },
      { onError: (error) => toast.error(apiErrorMessage(error, 'The dry run could not start.')) }
    );
  };

  return (
    <div className="space-y-3">
      <p className="text-muted-foreground text-sm">
        Test this workflow against sample content — actions are simulated, nothing is sent.
      </p>
      <Textarea
        rows={6}
        spellCheck={false}
        value={sample}
        onChange={(e) => setSample(e.target.value)}
        className="font-mono text-xs"
      />
      <Button size="sm" onClick={run} disabled={dryRun.isPending}>
        <IconPlay className="size-3.5" />
        {dryRun.isPending ? 'Running…' : 'Run simulation'}
      </Button>
      {dryRun.data && (
        <div className="rounded-md border px-3 py-2">
          <div className="flex items-center justify-between">
            <span className="text-sm font-medium">{dryRun.data.message || 'Result'}</span>
            <StatusBadge tone={dryRun.data.success ? 'success' : 'destructive'}>
              {dryRun.data.success ? 'Succeeded' : 'Failed'}
            </StatusBadge>
          </div>
          <ul className="text-muted-foreground mt-1.5 space-y-0.5 text-xs">
            {dryRun.data.actions.map((action, i) => (
              <li key={i}>
                {action.actionType}: {action.success ? 'ok' : action.errorMessage ?? 'failed'}
                {Object.keys(action.resolvedParameters).length > 0 &&
                  ` — ${JSON.stringify(action.resolvedParameters)}`}
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}
