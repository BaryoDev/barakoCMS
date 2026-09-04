import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { type ReactNode } from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ContentTypeDefinition } from '@/types/schema';

/**
 * The queries screen against a stubbed api, for the two things a hook test cannot see: what is
 * on screen while a request is still running, and what is on screen after one fails.
 */
vi.mock('@/lib/api', async () => {
    const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api');
    return { ...actual, api: { get: vi.fn(), post: vi.fn(), delete: vi.fn() } };
});

const { api, tokenStore } = await import('@/lib/api');
const { default: QueriesPage } = await import('./page');

const SLUG = 'active-subscribers';

const SCHEMA: ContentTypeDefinition = {
    name: 'Subscriber',
    displayName: 'Subscriber',
    fields: [
        { name: 'Email', displayName: 'Email', type: 'string', isRequired: false },
        { name: 'Status', displayName: 'Status', type: 'string', isRequired: false },
    ],
};

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

const ACME_ROW = { Email: 'a@acme.example', Status: 'Active' };
const GLOBEX_ROW = { Email: 'b@globex.example', Status: 'Active' };

function page<T>(items: T[]) {
    return {
        items,
        page: 1,
        pageSize: 100,
        totalItems: items.length,
        totalPages: 1,
        hasNextPage: false,
        hasPreviousPage: false,
    };
}

/** An unsigned token carrying only the claim the client reads. tenantOfToken never verifies. */
function tokenFor(tenant: string): string {
    return `eyJhbGciOiJub25lIn0.${btoa(JSON.stringify({ sub: 'u1', tenant }))}.sig`;
}

type Responder = (url: string) => Promise<{ data: unknown }>;

const getResponder: Responder = async (url) => {
    if (url === '/api/queries') return { data: page([SAVED]) };
    if (url === `/api/queries/${SLUG}`) return { data: SAVED };
    if (url === '/api/content-types') return { data: page([SCHEMA]) };
    throw new Error(`unexpected GET ${url}`);
};

function renderPage() {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const wrapper = ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );
    render(<QueriesPage />, { wrapper });
    return { client };
}

beforeEach(() => {
    vi.mocked(api.get).mockReset();
    vi.mocked(api.post).mockReset();
    vi.mocked(api.get).mockImplementation(getResponder as typeof api.get);
    tokenStore.set(tokenFor('acme'));
});

afterEach(() => {
    tokenStore.clear();
});

describe('the preview across a tenant switch', () => {
    it('shows none of the first tenant rows once the token changes, even before the new preview lands', async () => {
        let previewCalls = 0;
        let release!: (value: { data: unknown }) => void;
        const globexPreview = new Promise<{ data: unknown }>((resolve) => {
            release = resolve;
        });
        vi.mocked(api.post).mockImplementation((async (url: string) => {
            if (!url.endsWith('/preview')) throw new Error(`unexpected POST ${url}`);
            previewCalls += 1;
            // The first run answers as acme. Every run after the switch is held open, because the
            // window under test is the one where the new tenant's rows have not arrived yet.
            if (previewCalls === 1) return { data: { ok: true, count: 1, rows: [ACME_ROW] } };
            return globexPreview;
        }) as typeof api.post);

        const { client } = renderPage();

        fireEvent.click(await screen.findByLabelText('Open Active subscribers'));
        fireEvent.click(await screen.findByRole('button', { name: 'Preview' }));
        await screen.findByText('a@acme.example');

        // What useSwitchTenant does, in its order: the token first, then every query invalidated.
        act(() => tokenStore.set(tokenFor('globex')));
        act(() => void client.invalidateQueries());

        await waitFor(() => expect(previewCalls).toBeGreaterThan(1));
        expect(screen.queryByText('a@acme.example')).not.toBeInTheDocument();

        release({ data: { ok: true, count: 1, rows: [GLOBEX_ROW] } });
        await screen.findByText('b@globex.example');
        expect(screen.queryByText('a@acme.example')).not.toBeInTheDocument();
    });
});

describe('the list after a failed refetch', () => {
    it('keeps the table and its actions when the page it already holds is the last good answer', async () => {
        const { client } = renderPage();
        await screen.findByText('Active subscribers');

        vi.mocked(api.get).mockImplementation((async (url: string) => {
            if (url === '/api/queries') throw new Error('502');
            return getResponder(url);
        }) as typeof api.get);

        await act(async () => {
            await client.refetchQueries({ queryKey: ['queries'], exact: true });
            // The cache is in its error state when refetchQueries resolves, but the screen is not:
            // react-query hands observers their update on a setTimeout(0), and an assertion made
            // before that fires reads the render from before the failure and proves nothing.
            await new Promise((resolve) => setTimeout(resolve, 0));
        });

        // The precondition the fix is about: an error and cached data at the same time.
        expect(client.getQueryState(['queries'])?.status).toBe('error');
        expect(client.getQueryData(['queries'])).toBeDefined();

        expect(screen.getByText('Active subscribers')).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Delete Active subscribers' })).toBeInTheDocument();
        expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    });

    // The control for the test above: the same failure with nothing cached must still say so,
    // otherwise "no alert" would also pass on a screen that never shows one.
    it('shows the error panel when the first load fails and there is nothing to fall back on', async () => {
        vi.mocked(api.get).mockImplementation((async (url: string) => {
            if (url === '/api/queries') throw new Error('502');
            return getResponder(url);
        }) as typeof api.get);

        renderPage();

        const alert = await screen.findByRole('alert');
        expect(alert).toHaveTextContent(/load queries/);
        expect(screen.queryByText('Active subscribers')).not.toBeInTheDocument();
    });
});
