import { describe, it, expect, beforeEach, vi } from 'vitest';
import React, { type ReactNode } from 'react';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

/**
 * The preview cache, and what has to drop out of it.
 *
 * A preview is held with `staleTime: Infinity` on purpose: it is the snapshot the operator asked
 * for, and it must not swap itself out under them. That is only safe while a save drops the entry,
 * because otherwise the screen answers "the rows this query returns right now" with the rows of the
 * definition that was there before the edit, and makes no request to find out.
 *
 * These drive the hooks against a stubbed api, counting the preview calls, because the count is the
 * only thing that separates a re-run from a cache hit that looks identical on screen.
 */
vi.mock('@/lib/api', async () => {
    const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api');
    return { ...actual, api: { get: vi.fn(), post: vi.fn(), delete: vi.fn() } };
});

const { api } = await import('@/lib/api');
const { useQueryPreview, useSaveQuery, useDeleteQuery } = await import('./use-queries');

const SLUG = 'active-subscribers';

const BEFORE = { Email: 'before-edit@example.com', Status: 'Active' };
const AFTER = { Email: 'after-edit@example.com', Status: 'Active' };

const SAVED = {
    id: 'q1',
    name: 'Active subscribers',
    slug: SLUG,
    contentType: 'Subscriber',
    filters: [],
    sortField: null,
    descending: false,
    limit: 7,
    fields: ['Email', 'Status'],
    createdAt: '2026-09-01T00:00:00Z',
    updatedAt: '2026-09-01T00:00:00Z',
};

const INPUT = {
    name: SAVED.name,
    slug: SAVED.slug,
    contentType: SAVED.contentType,
    filters: [],
    sortField: null,
    descending: false,
    limit: 7,
    fields: SAVED.fields,
};

/** What the server would return from the next preview run. Moved by the tests. */
let rows = [BEFORE];

function previewCalls(): number {
    return vi.mocked(api.post).mock.calls.filter(([url]) => String(url).endsWith('/preview')).length;
}

function harness() {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const wrapper = ({ children }: { children: ReactNode }) =>
        React.createElement(QueryClientProvider, { client }, children);
    return { wrapper };
}

/** api.post is typed as axios's, so the stub answers with the one field the hooks read. */
type Responder = (url: string) => Promise<{ data: unknown }>;

const respond: Responder = async (url) =>
    url.endsWith('/preview')
        ? { data: { ok: true, count: rows.length, rows } }
        : { data: SAVED };

beforeEach(() => {
    rows = [BEFORE];
    vi.mocked(api.post).mockReset();
    vi.mocked(api.delete).mockReset();
    vi.mocked(api.post).mockImplementation(respond as typeof api.post);
    vi.mocked(api.delete).mockImplementation((async () => ({ data: undefined })) as typeof api.delete);
});

describe('the preview cache after a save', () => {
    it('re-runs the query when a preview of that slug is reopened after saving it', async () => {
        const { wrapper } = harness();

        const first = renderHook(() => useQueryPreview(SLUG), { wrapper });
        await waitFor(() => expect(first.result.current.data?.rows).toEqual([BEFORE]));
        // The operator previewed a different query, so this observer goes away and the rows of this
        // one stay in the cache. That is the state "Save and preview" comes back into.
        first.unmount();

        rows = [AFTER];

        const save = renderHook(() => useSaveQuery(), { wrapper });
        await save.result.current.mutateAsync(INPUT);

        const second = renderHook(() => useQueryPreview(SLUG), { wrapper });
        await waitFor(() => expect(previewCalls()).toBe(2), { timeout: 2000 });
        await waitFor(() => expect(second.result.current.data?.rows).toEqual([AFTER]));
    });

    it('re-runs a preview that is already on screen, so a plain Save does not leave stale rows', async () => {
        const { wrapper } = harness();

        const preview = renderHook(() => useQueryPreview(SLUG), { wrapper });
        await waitFor(() => expect(preview.result.current.data?.rows).toEqual([BEFORE]));

        rows = [AFTER];

        const save = renderHook(() => useSaveQuery(), { wrapper });
        await save.result.current.mutateAsync(INPUT);

        await waitFor(() => expect(previewCalls()).toBe(2), { timeout: 2000 });
        await waitFor(() => expect(preview.result.current.data?.rows).toEqual([AFTER]));
    });

    it('leaves the previews of other queries alone, since nothing about them changed', async () => {
        const { wrapper } = harness();

        const other = renderHook(() => useQueryPreview('lapsed-subscribers'), { wrapper });
        await waitFor(() => expect(other.result.current.data?.rows).toEqual([BEFORE]));

        const save = renderHook(() => useSaveQuery(), { wrapper });
        await save.result.current.mutateAsync(INPUT);

        // One call, from the mount above. A save of a different slug must not re-run this one.
        await new Promise((resolve) => setTimeout(resolve, 50));
        expect(previewCalls()).toBe(1);
    });
});

describe('the preview cache after a delete', () => {
    it('drops the rows, so a slug recreated later does not serve the deleted query rows', async () => {
        const { wrapper } = harness();

        const first = renderHook(() => useQueryPreview(SLUG), { wrapper });
        await waitFor(() => expect(first.result.current.data?.rows).toEqual([BEFORE]));
        first.unmount();

        const remove = renderHook(() => useDeleteQuery(), { wrapper });
        await remove.result.current.mutateAsync(SLUG);

        rows = [AFTER];

        const second = renderHook(() => useQueryPreview(SLUG), { wrapper });
        await waitFor(() => expect(previewCalls()).toBe(2), { timeout: 2000 });
        await waitFor(() => expect(second.result.current.data?.rows).toEqual([AFTER]));
    });
});
