import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import { useRetryAttempt } from './use-workflow-runs';

vi.mock('@/lib/api', () => ({
  api: { get: vi.fn(), post: vi.fn() },
}));

const post = vi.mocked(api.post);

function harness() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const invalidated: unknown[][] = [];
  const original = client.invalidateQueries.bind(client);
  vi.spyOn(client, 'invalidateQueries').mockImplementation((filters) => {
    invalidated.push((filters?.queryKey ?? []) as unknown[]);
    return original(filters);
  });

  function wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  }

  return { wrapper, invalidated };
}

describe('useRetryAttempt', () => {
  beforeEach(() => {
    post.mockReset();
    post.mockResolvedValue({ data: { id: 'run-1', status: 'Pending' } });
  });

  it('posts to the run and ordinal the operator pressed', async () => {
    const { wrapper } = harness();
    const { result } = renderHook(() => useRetryAttempt(), { wrapper });

    await result.current.mutateAsync({ runId: 'run-1', ordinal: 2 });

    expect(post).toHaveBeenCalledWith('/api/workflow-runs/run-1/actions/2/retry', {});
  });

  it('refetches the run and the list rather than painting on the response body', async () => {
    // The endpoint returns the run as it stood at the moment of the write, and the runner can claim
    // the attempt a tick later, so rendering that body would show a Pending action that is already
    // Running.
    const { wrapper, invalidated } = harness();
    const { result } = renderHook(() => useRetryAttempt(), { wrapper });

    const returned = await result.current.mutateAsync({ runId: 'run-1', ordinal: 2 });

    expect(returned).toBeUndefined();
    await waitFor(() => {
      expect(invalidated).toEqual([['workflow-run', 'run-1'], ['workflow-runs']]);
    });
  });

  it('refetches nothing when the retry was refused, so the screen keeps what the server holds', async () => {
    const { wrapper, invalidated } = harness();
    const { result } = renderHook(() => useRetryAttempt(), { wrapper });

    post.mockRejectedValueOnce(new Error('409'));

    await expect(result.current.mutateAsync({ runId: 'run-1', ordinal: 1 })).rejects.toThrow('409');
    expect(invalidated).toEqual([]);
  });
});
