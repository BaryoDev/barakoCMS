import { describe, it, expect, beforeEach, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ContentTypeDefinition } from '@/types/schema';

/**
 * The limit box, read back as a number. The box holds text, and the conversion is where a value
 * the operator never typed can appear: parseInt reads "1.5" as 1 and "1e2" as 1, both inside the
 * ceiling, and the save carries them.
 */
vi.mock('@/lib/api', async () => {
    const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api');
    return { ...actual, api: { get: vi.fn(), post: vi.fn(), delete: vi.fn() } };
});

const { api } = await import('@/lib/api');
const { QueryBuilder } = await import('./query-builder');

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
    slug: 'active-subscribers',
    contentType: 'Subscriber',
    filters: [],
    sortField: null,
    descending: false,
    limit: 7,
    fields: ['Email', 'Status'],
    createdAt: '2026-09-01T00:00:00Z',
    updatedAt: '2026-09-01T00:00:00Z',
};

function renderBuilder() {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
        <QueryClientProvider client={client}>
            <QueryBuilder
                saved={SAVED}
                contentTypes={[SCHEMA]}
                onSaved={() => {}}
                onPreview={() => {}}
                previewing={false}
                onClose={() => {}}
            />
        </QueryClientProvider>,
    );
}

function setLimit(value: string) {
    fireEvent.change(screen.getByLabelText('Limit'), { target: { value } });
}

const saveButton = () => screen.getByRole('button', { name: 'Save' });

beforeEach(() => {
    vi.mocked(api.post).mockReset();
    vi.mocked(api.post).mockImplementation((async (_url: string, body: { limit: number }) => ({
        data: { ...SAVED, limit: body.limit },
    })) as typeof api.post);
});

describe('the limit box', () => {
    it('refuses a decimal rather than saving it truncated', async () => {
        renderBuilder();
        setLimit('1.5');

        expect(screen.getByRole('status')).toHaveTextContent('whole number');
        expect(saveButton()).toBeDisabled();

        fireEvent.click(saveButton());
        await new Promise((resolve) => setTimeout(resolve, 20));
        expect(api.post).not.toHaveBeenCalled();
    });

    it('saves an exponent as the number it names', async () => {
        renderBuilder();
        setLimit('1e2');

        expect(screen.queryByRole('status')).not.toBeInTheDocument();
        expect(saveButton()).toBeEnabled();

        fireEvent.click(saveButton());
        await waitFor(() =>
            expect(api.post).toHaveBeenCalledWith('/api/queries', expect.objectContaining({ limit: 100 })),
        );
    });

    it('refuses an exponent that lands on a fraction', () => {
        renderBuilder();
        setLimit('1e-1');

        expect(screen.getByRole('status')).toHaveTextContent('whole number');
        expect(saveButton()).toBeDisabled();
    });

    it('refuses an empty box rather than saving a default', () => {
        renderBuilder();
        setLimit('');

        expect(screen.getByRole('status')).toHaveTextContent('whole number');
        expect(saveButton()).toBeDisabled();
    });
});
