'use client';

import { useState } from 'react';
import { toast } from 'sonner';
import { EmptyState } from '@/components/patterns/empty-state';
import { ErrorState } from '@/components/patterns/error-state';
import { PageHeader } from '@/components/patterns/page-header';
import { PaginationControls } from '@/components/patterns/pagination-controls';
import { StatusBadge } from '@/components/patterns/status-badge';
import { TableSkeleton } from '@/components/patterns/table-skeleton';
import { Button } from '@/components/ui/button';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { IconRefresh, IconWorkflows } from '@/components/icons';
import { apiErrorMessage } from '@/lib/api';
import {
  ANY_STATUS,
  RUN_STATUSES,
  toneForRunStatus,
  useRetryAttempt,
  useWorkflowRun,
  useWorkflowRuns,
  type WorkflowRun,
} from '@/hooks/use-workflow-runs';
import { AttemptCard } from './attempt-card';

const CARD = 'bg-card rounded-xl border p-6 shadow-[var(--shadow-card)]';

function formatMoment(value: string | null | undefined): string {
  if (!value) return '';
  const at = new Date(value);
  return Number.isNaN(at.getTime()) ? '' : at.toLocaleString();
}

function RunFacts({ run }: { run: WorkflowRun }) {
  const facts: { label: string; value: string }[] = [
    { label: 'Trigger', value: run.triggerEvent },
    { label: 'Content type', value: run.contentType },
    { label: 'Content', value: run.contentId },
    { label: 'Started', value: formatMoment(run.createdAt) },
  ];

  const completed = formatMoment(run.completedAt);
  if (completed) facts.push({ label: 'Finished', value: completed });

  return (
    <dl className="mt-4 grid gap-x-6 gap-y-1.5 text-[13px] sm:grid-cols-2">
      {facts.map((fact) => (
        <div key={fact.label} className="flex justify-between gap-3">
          <dt className="text-muted-foreground shrink-0">{fact.label}</dt>
          <dd className="truncate font-mono">{fact.value}</dd>
        </div>
      ))}
    </dl>
  );
}

export default function WorkflowRunsPage() {
  const [status, setStatus] = useState<string>(ANY_STATUS);
  const [page, setPage] = useState(1);
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const runs = useWorkflowRuns({ page, pageSize: 25, status });
  const run = useWorkflowRun(selectedId);
  const retry = useRetryAttempt();

  const rows = runs.data?.items ?? [];

  async function onRetry(ordinal: number) {
    if (!selectedId) return;

    try {
      await retry.mutateAsync({ runId: selectedId, ordinal });
      toast.success('Queued that action to run again.');
    } catch (error) {
      // A 409 lands here, which is the guard working rather than the button breaking: the runner
      // claimed the attempt while the operator was reading. The endpoint's wording says so.
      toast.error(apiErrorMessage(error));
    }
  }

  return (
    <div className="space-y-6">
      <PageHeader
        title="Workflow runs"
        description="Every firing of a workflow, newest first. Open one to see what each of its actions did."
        actions={
          <div className="flex items-center gap-2">
            <Select
              value={status}
              onValueChange={(value) => {
                setStatus(value);
                // Page 3 of "everything" is rarely page 3 of one status, and staying there shows an
                // empty list for a filter that has matches.
                setPage(1);
              }}
            >
              <SelectTrigger className="w-44" size="sm" aria-label="Filter runs by status">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={ANY_STATUS}>Every status</SelectItem>
                {RUN_STATUSES.map((option) => (
                  <SelectItem key={option} value={option}>
                    {option}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <Button
              variant="outline"
              size="sm"
              disabled={runs.isFetching}
              onClick={() => {
                void runs.refetch();
                if (selectedId) void run.refetch();
              }}
            >
              <IconRefresh />
              Refresh
            </Button>
          </div>
        }
      />

      <div className="grid items-start gap-4 lg:grid-cols-2">
        <section className={CARD}>
          <h2 className="text-[15px] font-bold">Runs</h2>

          {runs.isLoading ? (
            <div className="mt-4">
              <TableSkeleton rows={6} />
            </div>
          ) : runs.isError ? (
            <ErrorState className="mt-4" entity="workflow runs" onRetry={() => void runs.refetch()} />
          ) : rows.length === 0 ? (
            <EmptyState
              className="mt-4"
              icon={IconWorkflows}
              title="No runs to show"
              description={
                status === ANY_STATUS
                  ? 'Nothing has fired a workflow yet. A run appears here the moment one does.'
                  : `No run is currently ${status}. Try another status.`
              }
            />
          ) : (
            <>
              <Table className="mt-4">
                <TableHeader>
                  <TableRow>
                    <TableHead className="w-0">
                      <span className="sr-only">Open</span>
                    </TableHead>
                    <TableHead>Status</TableHead>
                    <TableHead>Workflow</TableHead>
                    <TableHead>Trigger</TableHead>
                    <TableHead>Started</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {rows.map((row) => (
                    <TableRow key={row.id} className={row.id === selectedId ? 'bg-accent' : undefined}>
                      <TableCell>
                        {/* A radio rather than a click on the row. The choice is one run out of
                            several, and a row that only answers a mouse cannot be opened from the
                            keyboard. */}
                        <input
                          type="radio"
                          name="run"
                          aria-label={`Open the ${row.workflowName} run started ${formatMoment(row.createdAt)}`}
                          checked={row.id === selectedId}
                          onChange={() => setSelectedId(row.id)}
                        />
                      </TableCell>
                      <TableCell>
                        <StatusBadge tone={toneForRunStatus(row.status)}>{row.status}</StatusBadge>
                      </TableCell>
                      <TableCell className="max-w-40 truncate text-[13px] font-bold">
                        {row.workflowName}
                      </TableCell>
                      <TableCell className="text-muted-foreground max-w-32 truncate font-mono text-[12px]">
                        {row.triggerEvent}
                      </TableCell>
                      <TableCell className="text-muted-foreground text-[12px] whitespace-nowrap">
                        {formatMoment(row.createdAt)}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              {runs.data && <PaginationControls page={runs.data} onPageChange={setPage} />}
            </>
          )}
        </section>

        <section className={CARD}>
          {!selectedId ? (
            <EmptyState
              icon={IconWorkflows}
              title="Choose a run"
              description="Its actions appear here in the order they execute, with what each one did and whether it can be retried."
            />
          ) : run.isLoading ? (
            <TableSkeleton rows={4} />
          ) : run.isError ? (
            <ErrorState entity="that run" onRetry={() => void run.refetch()} />
          ) : run.data ? (
            <>
              <div className="flex flex-wrap items-center gap-2">
                <h2 className="text-[15px] font-bold">{run.data.workflowName}</h2>
                <StatusBadge tone={toneForRunStatus(run.data.status)}>{run.data.status}</StatusBadge>
              </div>

              <RunFacts run={run.data} />

              <h3 className="mt-6 text-[13px] font-bold">Actions</h3>
              {run.data.actions.length === 0 ? (
                <p className="text-muted-foreground mt-1 text-[13px]">
                  This run carried no actions, so it completed with nothing to do.
                </p>
              ) : (
                <ol aria-label="Actions" className="mt-3 space-y-3">
                  {run.data.actions.map((attempt) => (
                    <AttemptCard
                      key={attempt.ordinal}
                      attempt={attempt}
                      retrying={retry.isPending}
                      onRetry={(ordinal) => void onRetry(ordinal)}
                    />
                  ))}
                </ol>
              )}
            </>
          ) : null}
        </section>
      </div>
    </div>
  );
}
