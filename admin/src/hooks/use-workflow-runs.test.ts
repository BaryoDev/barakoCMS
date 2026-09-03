import { describe, expect, it } from 'vitest';
import {
  ANY_STATUS,
  formatDuration,
  isRetryable,
  runListParams,
  toneForAttemptStatus,
  toneForRunStatus,
} from './use-workflow-runs';

describe('runListParams', () => {
  it('always sends a bounded page, so the list cannot be asked for everything', () => {
    expect(runListParams({})).toEqual({ page: 1, pageSize: 25 });
  });

  it('sends the status when one is chosen', () => {
    expect(runListParams({ status: 'Failed' })).toEqual({ page: 1, pageSize: 25, status: 'Failed' });
  });

  it('leaves the status out for "every status", which the API has no name for', () => {
    // Not status: 'all'. The endpoint refuses a status it cannot parse with a 400 rather than
    // dropping the filter, so sending this would turn "show me everything" into an error.
    expect(runListParams({ status: ANY_STATUS })).toEqual({ page: 1, pageSize: 25 });
  });

  it('leaves out a blank or whitespace status for the same reason', () => {
    expect(runListParams({ status: '' })).toEqual({ page: 1, pageSize: 25 });
    expect(runListParams({ status: '   ' })).toEqual({ page: 1, pageSize: 25 });
  });

  it('sends contentId when one is set and leaves it out when it is not', () => {
    expect(runListParams({ contentId: 'abc' })).toEqual({ page: 1, pageSize: 25, contentId: 'abc' });
    expect(runListParams({ contentId: '  ' })).toEqual({ page: 1, pageSize: 25 });
  });

  it('carries the page and page size through', () => {
    expect(runListParams({ page: 4, pageSize: 10, status: 'Running' })).toEqual({
      page: 4,
      pageSize: 10,
      status: 'Running',
    });
  });
});

describe('isRetryable', () => {
  it('offers a retry on a failed action', () => {
    expect(isRetryable({ status: 'Failed' })).toBe(true);
  });

  it('offers a retry on an unknown action, which is the timeout an operator has to judge', () => {
    expect(isRetryable({ status: 'Unknown' })).toBe(true);
  });

  it('never offers a retry on a succeeded action', () => {
    // The hazard the idempotency key exists for. The endpoint answers 409, so offering the button
    // could only produce an error toast, and the action would be sent twice if it did not.
    expect(isRetryable({ status: 'Succeeded' })).toBe(false);
  });

  it('never offers a retry on an action that is still running or already queued', () => {
    expect(isRetryable({ status: 'Running' })).toBe(false);
    expect(isRetryable({ status: 'Pending' })).toBe(false);
  });

  it('never offers a retry on a skipped action, whose condition was evaluated and not met', () => {
    expect(isRetryable({ status: 'Skipped' })).toBe(false);
  });

  it('refuses a status it does not recognise rather than guessing', () => {
    expect(isRetryable({ status: 'Quarantined' })).toBe(false);
  });
});

describe('formatDuration', () => {
  it('says nothing was recorded rather than printing a number for an absent duration', () => {
    expect(formatDuration(null)).toBe('not recorded');
    expect(formatDuration(undefined)).toBe('not recorded');
  });

  it('says nothing was recorded for a value no clock could produce', () => {
    expect(formatDuration(-1)).toBe('not recorded');
    expect(formatDuration(Number.NaN)).toBe('not recorded');
    expect(formatDuration(Number.POSITIVE_INFINITY)).toBe('not recorded');
  });

  it('keeps sub-second work in milliseconds', () => {
    expect(formatDuration(0)).toBe('0 ms');
    expect(formatDuration(42)).toBe('42 ms');
    expect(formatDuration(999)).toBe('999 ms');
  });

  it('switches to tenths of a second at a second', () => {
    expect(formatDuration(1000)).toBe('1.0 s');
    expect(formatDuration(1240)).toBe('1.2 s');
    expect(formatDuration(30500)).toBe('30.5 s');
  });

  it('rolls into minutes rather than printing a sixty-second second', () => {
    // 59,950 ms is under a minute, and one decimal place rounds it to "60.0 s", which is a duration
    // no clock shows. The guard is on the rounded value for exactly this input.
    expect(formatDuration(59_950)).toBe('1m 0s');
    expect(formatDuration(60_000)).toBe('1m 0s');
    expect(formatDuration(252_000)).toBe('4m 12s');
  });
});

describe('toneForRunStatus', () => {
  it('tints a partly failed run as a warning, not as a failure', () => {
    // Some of the actions went out. Colouring it the same as Failed tells an operator to retry a
    // run whose first two actions already posted.
    expect(toneForRunStatus('PartiallyFailed')).toBe('warning');
    expect(toneForRunStatus('Failed')).toBe('destructive');
  });

  it('tints the settled and in-flight states apart', () => {
    expect(toneForRunStatus('Succeeded')).toBe('success');
    expect(toneForRunStatus('Running')).toBe('accent');
    expect(toneForRunStatus('Pending')).toBe('muted');
  });

  it('falls back to muted for a status this build has not heard of', () => {
    expect(toneForRunStatus('Abandoned')).toBe('muted');
  });
});

describe('toneForAttemptStatus', () => {
  it('tints an unknown attempt as a warning, since it is neither a success nor a proven failure', () => {
    expect(toneForAttemptStatus('Unknown')).toBe('warning');
  });

  it('tints the rest by outcome', () => {
    expect(toneForAttemptStatus('Succeeded')).toBe('success');
    expect(toneForAttemptStatus('Failed')).toBe('destructive');
    expect(toneForAttemptStatus('Running')).toBe('accent');
    expect(toneForAttemptStatus('Pending')).toBe('muted');
    expect(toneForAttemptStatus('Skipped')).toBe('muted');
  });
});
