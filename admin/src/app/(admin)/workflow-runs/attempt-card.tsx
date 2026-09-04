'use client';

import { StatusBadge } from '@/components/patterns/status-badge';
import { Button } from '@/components/ui/button';
import { IconRefresh } from '@/components/icons';
import {
  formatDuration,
  isRetryable,
  toneForAttemptStatus,
  type WorkflowActionAttempt,
} from '@/hooks/use-workflow-runs';

/** A timestamp in the reader's locale, or nothing at all. A row for a date nobody has is noise. */
function formatMoment(value: string | null | undefined): string {
  if (!value) return '';
  const at = new Date(value);
  return Number.isNaN(at.getTime()) ? '' : at.toLocaleString();
}

interface AttemptCardProps {
  attempt: WorkflowActionAttempt;
  onRetry: (ordinal: number) => void;
  /** True while a retry is in flight anywhere in the run, so the button cannot be pressed twice. */
  retrying: boolean;
}

/**
 * One action of a run: what it was, how it went, and the retry button when retrying it is safe.
 *
 * The button's presence is `isRetryable` and nothing else, which is why that lives in the hook with
 * its own tests. Offering it on a succeeded action is the hazard the idempotency key was added for.
 */
export function AttemptCard({ attempt, onRetry, retrying }: AttemptCardProps) {
  const facts: { label: string; value: string }[] = [
    { label: 'Attempts', value: String(attempt.attempts) },
    { label: 'Took', value: formatDuration(attempt.durationMs) },
  ];

  if (attempt.responseStatus !== null && attempt.responseStatus !== undefined) {
    facts.push({ label: 'Response', value: String(attempt.responseStatus) });
  }

  const finished = formatMoment(attempt.completedAt);
  if (finished) facts.push({ label: 'Finished', value: finished });

  const next = formatMoment(attempt.nextAttemptAt);
  if (next) facts.push({ label: 'Next attempt', value: next });

  return (
    <li className="rounded-lg border p-4">
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-muted-foreground font-mono text-[12px] font-bold tabular-nums">
          {attempt.ordinal}
        </span>
        <span className="text-[13px] font-bold">{attempt.actionType}</span>
        <StatusBadge tone={toneForAttemptStatus(attempt.status)}>{attempt.status}</StatusBadge>

        {isRetryable(attempt) && (
          <Button
            className="ml-auto"
            size="sm"
            variant="outline"
            disabled={retrying}
            aria-label={`Retry action ${attempt.ordinal}, ${attempt.actionType}`}
            onClick={() => onRetry(attempt.ordinal)}
          >
            <IconRefresh />
            {retrying ? 'Queueing...' : 'Retry'}
          </Button>
        )}
      </div>

      <dl className="mt-3 grid gap-x-6 gap-y-1 text-[13px] sm:grid-cols-2">
        {facts.map((fact) => (
          <div key={fact.label} className="flex justify-between gap-3">
            <dt className="text-muted-foreground">{fact.label}</dt>
            <dd className="font-mono tabular-nums">{fact.value}</dd>
          </div>
        ))}
      </dl>

      {attempt.error && (
        <pre className="bg-muted mt-3 max-h-40 overflow-auto rounded-md p-3 text-[12px] whitespace-pre-wrap">
          {attempt.error}
        </pre>
      )}
    </li>
  );
}
