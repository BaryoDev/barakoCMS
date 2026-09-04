import { describe, it, expect, beforeEach, vi } from 'vitest';
import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { Connector } from '@/hooks/use-connectors';

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

function renderPage(items: Connector[] = []) {
    vi.mocked(api.get).mockResolvedValue({ data: { items, page: 1, pageSize: 20, total: items.length } });
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        React.createElement(QueryClientProvider, { client }, React.createElement(ConnectorsPage)),
    );
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
