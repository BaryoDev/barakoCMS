import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { type ReactNode } from 'react';
import { act, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

/**
 * The two reads across a tenant switch. A hook test rather than a page test, because the window
 * under test is the one between the token changing and the new tenant's answer landing, and the
 * only thing that matters in it is what the hook hands back.
 */
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api');
  return { ...actual, api: { get: vi.fn(), post: vi.fn() } };
});

const { api, tokenStore } = await import('@/lib/api');
const { useWorkflowRuns, useWorkflowRun } = await import('./use-workflow-runs');

/** An unsigned token carrying only the claim the client reads. tenantOfToken never verifies. */
function tokenFor(tenant: string): string {
  return `eyJhbGciOiJub25lIn0.${btoa(JSON.stringify({ sub: 'u1', tenant }))}.sig`;
}

function run(id: string, workflowName: string) {
  return { id, workflowName, status: 'Succeeded', startedAt: '2026-09-01T00:00:00Z', attempts: [] };
}

function List() {
  const runs = useWorkflowRuns({ page: 1, pageSize: 20 });
  return <ul>{(runs.data?.items ?? []).map((r) => <li key={r.id}>{r.workflowName}</li>)}</ul>;
}

function Detail() {
  const detail = useWorkflowRun('r1');
  return <p>{detail.data?.workflowName ?? 'nothing yet'}</p>;
}

function renderWith(node: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<QueryClientProvider client={client}>{node}</QueryClientProvider>);
  return client;
}

beforeEach(() => {
  vi.mocked(api.get).mockReset();
  tokenStore.set(tokenFor('acme'));
});

afterEach(() => {
  tokenStore.clear();
});

describe('the run caches across a tenant switch', () => {
  it('shows none of the first tenant runs once the token changes, even before the new list lands', async () => {
    let calls = 0;
    let release!: (value: { data: unknown }) => void;
    const held = new Promise<{ data: unknown }>((resolve) => {
      release = resolve;
    });
    vi.mocked(api.get).mockImplementation((async () => {
      calls += 1;
      if (calls === 1) {
        return { data: { items: [run('r1', 'Announce for acme')], page: 1, pageSize: 20, totalItems: 1, totalPages: 1, hasNextPage: false, hasPreviousPage: false } };
      }
      return held;
    }) as typeof api.get);

    const client = renderWith(<List />);
    await screen.findByText('Announce for acme');

    // What useSwitchTenant does, in its order: the token first, then every query invalidated.
    act(() => tokenStore.set(tokenFor('globex')));
    act(() => void client.invalidateQueries());

    await waitFor(() => expect(calls).toBeGreaterThan(1));
    expect(screen.queryByText('Announce for acme')).not.toBeInTheDocument();

    release({ data: { items: [run('r9', 'Announce for globex')], page: 1, pageSize: 20, totalItems: 1, totalPages: 1, hasNextPage: false, hasPreviousPage: false } });
    await screen.findByText('Announce for globex');
    expect(screen.queryByText('Announce for acme')).not.toBeInTheDocument();
  });

  it('shows none of the first tenant detail once the token changes', async () => {
    let calls = 0;
    vi.mocked(api.get).mockImplementation((async () => {
      calls += 1;
      if (calls === 1) return { data: run('r1', 'Acme detail') };
      return new Promise(() => undefined);
    }) as typeof api.get);

    const client = renderWith(<Detail />);
    await screen.findByText('Acme detail');

    act(() => tokenStore.set(tokenFor('globex')));
    act(() => void client.invalidateQueries());

    await waitFor(() => expect(calls).toBeGreaterThan(1));
    expect(screen.queryByText('Acme detail')).not.toBeInTheDocument();
    expect(screen.getByText('nothing yet')).toBeInTheDocument();
  });
});
