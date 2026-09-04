import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { AttemptCard } from './attempt-card';
import type { WorkflowActionAttempt } from '@/hooks/use-workflow-runs';

function attempt(overrides: Partial<WorkflowActionAttempt> = {}): WorkflowActionAttempt {
  return {
    ordinal: 2,
    actionType: 'Webhook',
    status: 'Failed',
    attempts: 3,
    durationMs: 1240,
    responseStatus: 503,
    error: 'Service Unavailable',
    completedAt: null,
    nextAttemptAt: null,
    ...overrides,
  };
}

function renderCard(a: WorkflowActionAttempt, onRetry = vi.fn(), retrying = false) {
  render(
    <ol>
      <AttemptCard attempt={a} onRetry={onRetry} retrying={retrying} />
    </ol>,
  );
  return onRetry;
}

describe('AttemptCard', () => {
  it('offers a retry on a failed action and reports the ordinal it belongs to', () => {
    const onRetry = renderCard(attempt());

    fireEvent.click(screen.getByRole('button', { name: /retry action 2/i }));

    // The ordinal, not the array index. The endpoint addresses the action by ordinal, so sending
    // the wrong one retries a different action than the one the operator pressed.
    expect(onRetry).toHaveBeenCalledWith(2);
  });

  it('offers a retry on an unknown action, the timeout only a person can judge', () => {
    renderCard(attempt({ status: 'Unknown', error: 'The request timed out.' }));

    expect(screen.getByRole('button', { name: /retry action 2/i })).toBeInTheDocument();
  });

  it('does not offer a retry on a succeeded action', () => {
    // The hazard the idempotency key exists for: retrying a succeeded action sends it a second
    // time. The UI must not put the button on screen at all.
    renderCard(attempt({ status: 'Succeeded', error: null, responseStatus: 200 }));

    expect(screen.queryByRole('button', { name: /retry/i })).not.toBeInTheDocument();
  });

  it('does not offer a retry while the action is running', () => {
    renderCard(attempt({ status: 'Running', error: null, responseStatus: null }));

    expect(screen.queryByRole('button', { name: /retry/i })).not.toBeInTheDocument();
  });

  it('does not offer a retry on an action that is already queued or was skipped', () => {
    renderCard(attempt({ status: 'Pending', error: null, responseStatus: null }));
    expect(screen.queryByRole('button', { name: /retry/i })).not.toBeInTheDocument();

    renderCard(attempt({ ordinal: 3, status: 'Skipped', error: null, responseStatus: null }));
    expect(screen.queryByRole('button', { name: /retry/i })).not.toBeInTheDocument();
  });

  it('disables the button while a retry is in flight, so it cannot be pressed twice', () => {
    renderCard(attempt(), vi.fn(), true);

    expect(screen.getByRole('button', { name: /retry action 2/i })).toBeDisabled();
  });

  it('shows the attempt count, the duration and the response status', () => {
    renderCard(attempt());

    expect(screen.getByText('Attempts')).toBeInTheDocument();
    expect(screen.getByText('3')).toBeInTheDocument();
    expect(screen.getByText('1.2 s')).toBeInTheDocument();
    expect(screen.getByText('503')).toBeInTheDocument();
  });

  it('shows the error when there is one', () => {
    renderCard(attempt({ error: 'Connection refused by hooks.example.com' }));

    expect(screen.getByText('Connection refused by hooks.example.com')).toBeInTheDocument();
  });

  it('leaves out the rows for facts the run does not carry', () => {
    renderCard(attempt({ responseStatus: null, completedAt: null, nextAttemptAt: null }));

    // A label with nothing under it reads as missing data rather than as a fact that does not apply.
    expect(screen.queryByText('Response')).not.toBeInTheDocument();
    expect(screen.queryByText('Finished')).not.toBeInTheDocument();
    expect(screen.queryByText('Next attempt')).not.toBeInTheDocument();
  });

  it('shows when the next automatic attempt is due', () => {
    renderCard(attempt({ status: 'Pending', nextAttemptAt: '2026-09-03T10:15:00Z' }));

    expect(screen.getByText('Next attempt')).toBeInTheDocument();
  });
});
