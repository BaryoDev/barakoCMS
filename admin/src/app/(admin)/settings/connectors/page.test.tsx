import { describe, it, expect, beforeEach, vi } from 'vitest';
import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CONNECTORS_PAGE_SIZE, type Connector } from '@/hooks/use-connectors';
import type { Paginated } from '@/lib/api';

/**
 * The dialog is where the operator's typing meets the rules the server enforces, and none of that
 * is reachable from the hook tests. The first case here is the one that matters: re-slugifying on
 * every keystroke ate the hyphen out of "company-jira", and the slug cannot be corrected after a
 * create because UpdateConnectorEndpoint overwrites it with the stored one.
 */
vi.mock('@/lib/api', async () => {
    const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api');
    return {
        ...actual,
        api: { get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn() },
    };
});

vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

// Radix sizes its dialog with one, and jsdom ships no implementation.
globalThis.ResizeObserver ??= class {
    observe() {}
    unobserve() {}
    disconnect() {}
};

const { api } = await import('@/lib/api');
const { default: ConnectorsPage } = await import('./page');

function connector(overrides: Partial<Connector> = {}): Connector {
    return {
        id: '11111111-1111-1111-1111-111111111111',
        name: 'Company Jira',
        slug: 'company-jira',
        auth: 'Basic',
        baseUrl: 'https://jira.example.com',
        settings: { Username: 'reporting-bot' },
        secretKeys: ['Password'],
        enabled: true,
        probePath: '/',
        lastTestedAt: null,
        lastTestResult: null,
        createdAt: '2026-01-01T00:00:00Z',
        updatedAt: '2026-01-01T00:00:00Z',
        ...overrides,
    };
}

/** The shape ListConnectorsEndpoint answers with, for one page out of `totalItems`. */
function pageOf(items: Connector[], page = 1, totalItems = items.length): Paginated<Connector> {
    const totalPages = Math.ceil(totalItems / CONNECTORS_PAGE_SIZE);
    return {
        items,
        page,
        pageSize: CONNECTORS_PAGE_SIZE,
        totalItems,
        totalPages,
        hasNextPage: page < totalPages,
        hasPreviousPage: page > 1,
    };
}

function renderWith(client: QueryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })) {
    return render(
        React.createElement(QueryClientProvider, { client }, React.createElement(ConnectorsPage)),
    );
}

function renderPage(items: Connector[] = []) {
    vi.mocked(api.get).mockResolvedValue({ data: pageOf(items) });
    return renderWith();
}

/** The page number the list asked for on a given call to the API. */
function requestedPage(call: number): number {
    const config = vi.mocked(api.get).mock.calls[call][1] as { params: { page: number } };
    return config.params.page;
}

/** One change event per character, the way a browser delivers them. */
function typeInto(field: HTMLInputElement, text: string) {
    for (const character of text) {
        fireEvent.change(field, { target: { value: field.value + character } });
    }
}

/** Select all, then type, which is how an operator replaces a derived slug. */
function retype(field: HTMLInputElement, text: string) {
    fireEvent.change(field, { target: { value: '' } });
    typeInto(field, text);
}

const slugField = () => screen.getByLabelText('Slug') as HTMLInputElement;
const nameField = () => screen.getByLabelText('Name') as HTMLInputElement;
const baseUrlField = () => screen.getByLabelText('Base URL') as HTMLInputElement;

async function openCreate() {
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'New connector' }));
    await screen.findByRole('dialog');
}

async function openEdit(item: Connector) {
    renderPage([item]);
    fireEvent.click(await screen.findByRole('button', { name: `Edit ${item.name}` }));
    await screen.findByRole('dialog');
}

beforeEach(() => {
    vi.mocked(api.get).mockReset();
    vi.mocked(api.post).mockReset();
    vi.mocked(api.put).mockReset();
    vi.mocked(api.delete).mockReset();
});

describe('the slug field', () => {
    it('keeps a hyphen the operator types', async () => {
        await openCreate();

        typeInto(slugField(), 'company-jira');

        expect(slugField().value).toBe('company-jira');
    });

    it('still derives the slug from the name until the operator edits it', async () => {
        await openCreate();

        typeInto(nameField(), 'Company Jira');

        expect(slugField().value).toBe('company-jira');
    });

    it('stops deriving once the operator has typed in it', async () => {
        await openCreate();

        retype(slugField(), 'jira');
        typeInto(nameField(), 'Company Jira');

        expect(slugField().value).toBe('jira');
    });

    it('sends what the operator typed', async () => {
        vi.mocked(api.post).mockResolvedValue({ data: connector() });
        await openCreate();

        typeInto(nameField(), 'Company Jira');
        retype(slugField(), 'company-jira-eu');
        typeInto(baseUrlField(), 'https://jira.example.com');
        fireEvent.click(screen.getByRole('button', { name: 'Add connector' }));

        await waitFor(() => expect(api.post).toHaveBeenCalled());
        expect(vi.mocked(api.post).mock.calls[0][1]).toMatchObject({ slug: 'company-jira-eu' });
    });

    // The server's rule is ^[a-z0-9][a-z0-9-]{0,62}$, and the slug is permanent after a create, so
    // a rejected save is the only good outcome for a slug it would refuse.
    it('will not save a slug the server would reject', async () => {
        await openCreate();

        typeInto(nameField(), 'Company Jira');
        typeInto(baseUrlField(), 'https://jira.example.com');
        retype(slugField(), 'Company Jira');

        expect(screen.getByRole('button', { name: 'Add connector' })).toBeDisabled();
        expect(screen.getByText(/The server will refuse that slug/)).toBeInTheDocument();
    });

    // aria-invalid on its own tells a screen reader the box is wrong and nothing else. The warning
    // has to be the box's description, or the reason is only there for somebody who can see it.
    it('reads the refusal to assistive tech through the slug box', async () => {
        await openCreate();

        retype(slugField(), 'Company Jira');

        expect(slugField()).toHaveAttribute('aria-invalid', 'true');
        expect(slugField()).toHaveAccessibleDescription(/The server will refuse that slug/);
    });

    it('describes only the rule while the slug is acceptable', async () => {
        await openCreate();

        retype(slugField(), 'company-jira');

        expect(slugField()).not.toHaveAttribute('aria-invalid', 'true');
        expect(slugField()).toHaveAccessibleDescription(/Lowercase letters, digits and hyphens/);
        expect(slugField()).not.toHaveAccessibleDescription(/The server will refuse that slug/);
    });

    it('will not save a slug that starts with a hyphen', async () => {
        await openCreate();

        typeInto(nameField(), 'Company Jira');
        typeInto(baseUrlField(), 'https://jira.example.com');
        retype(slugField(), '-jira');

        expect(screen.getByRole('button', { name: 'Add connector' })).toBeDisabled();
    });

    it('is not editable on an existing connector', async () => {
        await openEdit(connector());

        expect(slugField()).toBeDisabled();
        expect(slugField().value).toBe('company-jira');
    });
});

describe('the list', () => {
    /** Enough connectors for `total` to span more than one page, each with its own id and slug. */
    function many(count: number, from = 0): Connector[] {
        return Array.from({ length: count }, (_, i) =>
            connector({
                id: `00000000-0000-0000-0000-${String(from + i).padStart(12, '0')}`,
                name: `Connector ${from + i}`,
                slug: `connector-${from + i}`,
            }),
        );
    }

    it('asks the API for a page rather than for everything', async () => {
        renderPage([connector()]);
        await screen.findByText('Company Jira');

        expect(api.get).toHaveBeenCalledWith('/api/connectors', {
            params: { page: 1, pageSize: CONNECTORS_PAGE_SIZE },
        });
    });

    it('shows the controls and fetches the next page when there is one', async () => {
        const total = CONNECTORS_PAGE_SIZE + 1;
        vi.mocked(api.get).mockImplementation(async (_url, config) => {
            const page = (config as { params: { page: number } }).params.page;
            const items = page === 1 ? many(CONNECTORS_PAGE_SIZE) : many(1, CONNECTORS_PAGE_SIZE);
            return { data: pageOf(items, page, total) };
        });
        renderWith();
        await screen.findByText('Connector 0');

        fireEvent.click(screen.getByRole('button', { name: 'Next' }));

        await screen.findByText(`Connector ${CONNECTORS_PAGE_SIZE}`);
        expect(vi.mocked(api.get).mock.calls).toHaveLength(2);
        expect(requestedPage(1)).toBe(2);
    });

    it('hides the controls when everything fits on one page', async () => {
        renderPage([connector()]);
        await screen.findByText('Company Jira');

        expect(screen.queryByRole('button', { name: 'Next' })).not.toBeInTheDocument();
    });

    // The controls hide themselves once there is a single page, so deleting the only row on page 2
    // would strand the operator on an empty page with no way back.
    it('steps back a page after deleting the only row on a later page', async () => {
        const firstPage = many(CONNECTORS_PAGE_SIZE);
        const [last] = many(1, CONNECTORS_PAGE_SIZE);
        vi.mocked(api.get).mockImplementation(async (_url, config) => {
            const page = (config as { params: { page: number } }).params.page;
            // The server before the delete holds 26 rows; after it, 25, and page 2 is empty.
            const deleted = vi.mocked(api.delete).mock.calls.length > 0;
            const total = deleted ? CONNECTORS_PAGE_SIZE : CONNECTORS_PAGE_SIZE + 1;
            const items = page === 1 ? firstPage : deleted ? [] : [last];
            return { data: pageOf(items, page, total) };
        });
        vi.mocked(api.delete).mockResolvedValue({ data: undefined });
        renderWith();
        await screen.findByText('Connector 0');
        fireEvent.click(screen.getByRole('button', { name: 'Next' }));
        await screen.findByText(last.name);

        fireEvent.click(screen.getByRole('button', { name: `Delete ${last.name}` }));
        fireEvent.click(await screen.findByRole('button', { name: 'Delete' }));

        await waitFor(() => expect(api.delete).toHaveBeenCalled());
        await screen.findByText('Connector 0');
        const pages = vi.mocked(api.get).mock.calls.map((_, i) => requestedPage(i));
        expect(pages.at(-1)).toBe(1);
        expect(screen.queryByText('Nothing on this page')).not.toBeInTheDocument();
    });
});

describe('the credential box', () => {
    it('starts blank even when a credential is stored', async () => {
        await openEdit(connector());

        const box = screen.getByLabelText('Replace the stored Password') as HTMLInputElement;
        expect(box.value).toBe('');
        expect(box.type).toBe('password');
    });

    it('is left out of the save when nothing was typed', async () => {
        vi.mocked(api.put).mockResolvedValue({ data: connector() });
        await openEdit(connector());

        fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

        await waitFor(() => expect(api.put).toHaveBeenCalled());
        expect(vi.mocked(api.put).mock.calls[0][1]).toMatchObject({ secrets: undefined });
    });

    it('sends a typed credential under the name the sender looks for', async () => {
        vi.mocked(api.put).mockResolvedValue({ data: connector() });
        await openEdit(connector());

        typeInto(screen.getByLabelText('Replace the stored Password') as HTMLInputElement, 'hunter2');
        fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

        await waitFor(() => expect(api.put).toHaveBeenCalled());
        expect(vi.mocked(api.put).mock.calls[0][1]).toMatchObject({ secrets: { Password: 'hunter2' } });
    });
});

describe('the delete-a-stored-credential checkbox', () => {
    it('sends an empty value, which is what the endpoint reads as a delete', async () => {
        vi.mocked(api.put).mockResolvedValue({ data: connector() });
        await openEdit(connector());

        fireEvent.click(screen.getByLabelText(/Delete the stored Password/));
        fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

        await waitFor(() => expect(api.put).toHaveBeenCalled());
        expect(vi.mocked(api.put).mock.calls[0][1]).toMatchObject({ secrets: { Password: '' } });
    });

    // Ticking the box and then typing a replacement is a replacement, not a delete followed by a
    // set, because only one of the two can win and losing the typed value would be the bad one.
    it('loses to a credential typed in the same session', async () => {
        vi.mocked(api.put).mockResolvedValue({ data: connector() });
        await openEdit(connector());

        fireEvent.click(screen.getByLabelText(/Delete the stored Password/));
        typeInto(screen.getByLabelText('Replace the stored Password') as HTMLInputElement, 'hunter2');
        fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

        await waitFor(() => expect(api.put).toHaveBeenCalled());
        expect(vi.mocked(api.put).mock.calls[0][1]).toMatchObject({ secrets: { Password: 'hunter2' } });
    });
});
