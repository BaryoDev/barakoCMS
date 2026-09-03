'use client';

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, type Paginated } from '@/lib/api';
import type { Tone } from '@/components/patterns/status-badge';

// Mirrors barakoCMS/Features/WorkflowRuns/Endpoints.cs (camelCase over the wire).

/** The value the status filter uses for "do not filter". Not a status the API knows. */
export const ANY_STATUS = 'all';

/** RunStatus on the server. Widened to string on the DTOs so a new member parses rather than throws. */
export const RUN_STATUSES = ['Pending', 'Running', 'Succeeded', 'Failed', 'PartiallyFailed'] as const;

export type RunStatus = (typeof RUN_STATUSES)[number];

export interface WorkflowActionAttempt {
  ordinal: number;
  actionType: string;
  status: string;
  attempts: number;
  nextAttemptAt?: string | null;
  responseStatus?: number | null;
  /** Why it failed, truncated by the server. Never a response body. */
  error?: string | null;
  completedAt?: string | null;
  durationMs?: number | null;
}

export interface WorkflowRun {
  id: string;
  workflowDefinitionId: string;
  workflowName: string;
  contentId: string;
  contentType: string;
  triggerEvent: string;
  status: string;
  createdAt: string;
  completedAt?: string | null;
  actions: WorkflowActionAttempt[];
}

export interface WorkflowRunsQuery {
  page?: number;
  pageSize?: number;
  /** A RunStatus name, or ANY_STATUS / empty for every status. */
  status?: string;
  contentId?: string;
}

/**
 * The query string for the list endpoint, carrying only the filters that are set.
 *
 * A blank status has to be left out rather than sent through. `GET /api/workflow-runs?status=`
 * answers 400 with the list of valid names, because the endpoint refuses a filter it cannot parse
 * instead of dropping it, so sending the empty filter turns "show me everything" into an error.
 */
export function runListParams(query: WorkflowRunsQuery): Record<string, string | number> {
  const params: Record<string, string | number> = {
    page: query.page ?? 1,
    pageSize: query.pageSize ?? 25,
  };

  const status = query.status?.trim();
  if (status && status !== ANY_STATUS) params.status = status;

  const contentId = query.contentId?.trim();
  if (contentId) params.contentId = contentId;

  return params;
}

/**
 * Whether an operator may retry one action.
 *
 * Failed is the ordinary case. Unknown is a timeout, where the request may well have arrived and
 * only the response was lost, so retrying it is a decision to accept possible duplicate delivery,
 * and it is a decision only a person can make.
 *
 * Everything else is refused here so the button is never offered:
 *
 * - Succeeded would send the action a second time. The idempotency key exists because that hazard
 *   is real, and the endpoint answers 409, so offering the button only produces an error toast.
 * - Running is in flight on some node right now.
 * - Pending is already queued and the runner will pick it up without being asked.
 * - Skipped had its condition evaluated and not met. The run counts it as done.
 */
export function isRetryable(attempt: Pick<WorkflowActionAttempt, 'status'>): boolean {
  return attempt.status === 'Failed' || attempt.status === 'Unknown';
}

/**
 * A duration in words, at a precision an operator can act on.
 *
 * Sub-second stays in milliseconds, because the difference between 40 ms and 900 ms is the
 * difference between a local call and a provider having a bad day. Above a minute drops the
 * fraction, because nothing is decided by the tenth of a second in "4m 12s".
 */
export function formatDuration(ms: number | null | undefined): string {
  if (ms === null || ms === undefined || !Number.isFinite(ms) || ms < 0) return 'not recorded';
  if (ms < 1000) return `${Math.round(ms)} ms`;

  const seconds = ms / 1000;
  // Guarded on the rounded value, not on the raw milliseconds. 59,950 ms is under a minute but
  // renders as "60.0 s" once toFixed(1) has had it, which is a duration no clock shows.
  if (seconds < 59.95) return `${seconds.toFixed(1)} s`;

  const total = Math.round(seconds);
  return `${Math.floor(total / 60)}m ${total % 60}s`;
}

/**
 * The badge tint for a run.
 *
 * PartiallyFailed is warning rather than danger, and that is the whole reason the server keeps it as
 * a state: some of the actions went out. Colouring it the same as Failed would tell an operator to
 * retry a run whose first two actions already posted.
 */
export function toneForRunStatus(status: string): Tone {
  switch (status) {
    case 'Succeeded':
      return 'success';
    case 'Failed':
      return 'destructive';
    case 'PartiallyFailed':
      return 'warning';
    case 'Running':
      return 'accent';
    default:
      return 'muted';
  }
}

/** The badge tint for one action. Unknown is warning: it is neither a success nor a proven failure. */
export function toneForAttemptStatus(status: string): Tone {
  switch (status) {
    case 'Succeeded':
      return 'success';
    case 'Failed':
      return 'destructive';
    case 'Unknown':
      return 'warning';
    case 'Running':
      return 'accent';
    default:
      return 'muted';
  }
}

export function useWorkflowRuns(query: WorkflowRunsQuery) {
  const params = runListParams(query);

  return useQuery({
    queryKey: ['workflow-runs', params],
    queryFn: async () => {
      const response = await api.get<Paginated<WorkflowRun>>('/api/workflow-runs', { params });
      return response.data;
    },
  });
}

export function useWorkflowRun(id: string | null) {
  return useQuery({
    queryKey: ['workflow-run', id],
    queryFn: async () => {
      const response = await api.get<WorkflowRun>(`/api/workflow-runs/${id}`);
      return response.data;
    },
    enabled: !!id,
  });
}

/**
 * Queues one action to be attempted again.
 *
 * The response body is thrown away on purpose. The endpoint returns the run as it stood at the
 * moment of the write, and the runner can claim the attempt a tick later, so painting that body
 * onto the screen would show a Pending action that is already Running. Invalidating instead means
 * what the operator reads after pressing the button came from a fresh read.
 */
export function useRetryAttempt() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (input: { runId: string; ordinal: number }) => {
      await api.post(`/api/workflow-runs/${input.runId}/actions/${input.ordinal}/retry`, {});
    },
    onSuccess: (_result, input) => {
      void queryClient.invalidateQueries({ queryKey: ['workflow-run', input.runId] });
      void queryClient.invalidateQueries({ queryKey: ['workflow-runs'] });
    },
  });
}
