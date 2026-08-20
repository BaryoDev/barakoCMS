import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

/**
 * The unseen dot used to be driven by a hand-maintained CURRENT_VERSION constant, which sat at
 * 3.1.2 while the product shipped 3.20.1 — so the dot never appeared for anyone for months. It is
 * now driven by the version the running API reports. These guard that it is genuinely reading the
 * API's number, and that the dialog still never opens by itself.
 */
vi.mock('@/lib/api', async () => {
    const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api');
    return { ...actual, api: { get: vi.fn() } };
});

const { api } = await import('@/lib/api');
const { WhatsNew } = await import('./whats-new');

const SEEN_KEY = 'barako_whats_new_seen';

function renderWhatsNew() {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        React.createElement(QueryClientProvider, { client }, React.createElement(WhatsNew)),
    );
}

const dot = () => document.querySelector('[aria-label="What\'s new"] span[aria-hidden]');
const dialog = () => screen.queryByRole('dialog');

describe('the unseen dot', () => {
    beforeEach(() => {
        localStorage.clear();
        vi.mocked(api.get).mockReset();
    });
    afterEach(() => localStorage.clear());

    it('appears when the API reports a version this browser has not seen', async () => {
        localStorage.setItem(SEEN_KEY, '3.20.1');
        vi.mocked(api.get).mockResolvedValue({ data: { version: '3.21.0', swaggerEnabled: false } });

        renderWhatsNew();

        await waitFor(() => expect(dot()).toBeInTheDocument());
    });

    // The positive control's opposite: without this, a dot that is always on would pass the test
    // above. The version here matches, so the correct behaviour is no dot at all.
    it('stays away when the API version is the one already seen', async () => {
        localStorage.setItem(SEEN_KEY, '3.21.0');
        vi.mocked(api.get).mockResolvedValue({ data: { version: '3.21.0', swaggerEnabled: false } });

        renderWhatsNew();

        await waitFor(() => expect(api.get).toHaveBeenCalled());
        expect(dot()).not.toBeInTheDocument();
    });

    // This is the specific regression the old code had. 3.1.2 was the bundled constant; a browser
    // that had acknowledged it must still be told about 3.21.0.
    it('does not use a bundled constant, so an old acknowledgement still flags a new release', async () => {
        localStorage.setItem(SEEN_KEY, '3.1.2');
        vi.mocked(api.get).mockResolvedValue({ data: { version: '3.21.0', swaggerEnabled: false } });

        renderWhatsNew();

        await waitFor(() => expect(dot()).toBeInTheDocument());
    });

    it('clears on open, and records the version the API reported', async () => {
        localStorage.setItem(SEEN_KEY, '3.20.1');
        vi.mocked(api.get).mockResolvedValue({ data: { version: '3.21.0', swaggerEnabled: false } });

        renderWhatsNew();
        await waitFor(() => expect(dot()).toBeInTheDocument());

        fireEvent.click(screen.getByLabelText("What's new"));

        expect(localStorage.getItem(SEEN_KEY)).toBe('3.21.0');
        await waitFor(() => expect(dot()).not.toBeInTheDocument());
    });

    it('shows no dot while the API version is unknown', async () => {
        vi.mocked(api.get).mockRejectedValue(new Error('unreachable'));

        renderWhatsNew();

        await waitFor(() => expect(api.get).toHaveBeenCalled());
        expect(dot()).not.toBeInTheDocument();
    });
});

describe('the dialog', () => {
    beforeEach(() => {
        localStorage.clear();
        vi.mocked(api.get).mockReset();
    });

    it('does not open on its own, even on a brand new browser with a new version', async () => {
        vi.mocked(api.get).mockResolvedValue({ data: { version: '3.21.0', swaggerEnabled: false } });

        renderWhatsNew();

        await waitFor(() => expect(dot()).toBeInTheDocument());
        expect(dialog()).not.toBeInTheDocument();
    });

    // Paired with the assertion above: proves the dialog is reachable at all, so "never opens on
    // its own" is not passing because the dialog is broken.
    it('opens when the button is clicked', async () => {
        vi.mocked(api.get).mockResolvedValue({ data: { version: '3.21.0', swaggerEnabled: false } });

        renderWhatsNew();
        await waitFor(() => expect(api.get).toHaveBeenCalled());

        fireEvent.click(screen.getByLabelText("What's new"));

        expect(dialog()).toBeInTheDocument();
    });
});
