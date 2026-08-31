import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import {
    reportClientError,
    flushClientErrors,
    installClientErrorHandlers,
    __resetClientErrorsForTests,
} from './client-errors';
import { tokenStore } from './api';

/**
 * The reporter's job is to get faults to the API without ever becoming a fault itself. These cover the
 * batching contract and, more importantly, the guards: a failing send must stay silent, and a runaway
 * error loop must not turn into unbounded requests.
 */
describe('client error reporter', () => {
    let fetchMock: ReturnType<typeof vi.fn>;

    beforeEach(() => {
        __resetClientErrorsForTests();
        localStorage.clear();
        // The access token is a module variable now, so localStorage.clear() no longer reaches it
        // and a test that signs in leaves the next one authenticated.
        tokenStore.clear();
        fetchMock = vi.fn().mockResolvedValue({ ok: true });
        vi.stubGlobal('fetch', fetchMock);
        vi.useFakeTimers();
    });

    afterEach(() => {
        vi.useRealTimers();
        vi.unstubAllGlobals();
    });

    function bodyOf(call: number) {
        return JSON.parse(fetchMock.mock.calls[call][1].body as string);
    }

    it('posts a queued error to /api/client-errors after the debounce', async () => {
        reportClientError({ kind: 'error', message: 'boom' });
        expect(fetchMock).not.toHaveBeenCalled(); // batched, not sent per-error

        await vi.advanceTimersByTimeAsync(2500);

        expect(fetchMock).toHaveBeenCalledTimes(1);
        const [url, init] = fetchMock.mock.calls[0];
        expect(String(url)).toContain('/api/client-errors');
        expect(init.method).toBe('POST');
        expect(bodyOf(0).items[0]).toMatchObject({ kind: 'error', message: 'boom', severity: 'error' });
    });

    it('dedupes an identical repeated error', async () => {
        reportClientError({ kind: 'error', message: 'same' });
        reportClientError({ kind: 'error', message: 'same' });
        reportClientError({ kind: 'error', message: 'different' });

        await vi.advanceTimersByTimeAsync(2500);

        expect(bodyOf(0).items).toHaveLength(2);
    });

    it('stays silent when the send fails', async () => {
        fetchMock.mockRejectedValue(new Error('network down'));
        reportClientError({ kind: 'error', message: 'boom' });

        // The point: this must not reject, or a failed report becomes an unhandled rejection,
        // which the handler would capture and try to report — forever.
        await expect(flushClientErrors()).resolves.toBeUndefined();
    });

    it('caps how many times it sends in one session', async () => {
        for (let i = 0; i < 60; i++) {
            reportClientError({ kind: 'error', message: `err-${i}` });
            await vi.advanceTimersByTimeAsync(2500);
        }
        expect(fetchMock.mock.calls.length).toBeLessThanOrEqual(25);
    });

    it('captures window error and unhandledrejection events', async () => {
        const cleanup = installClientErrorHandlers();

        window.dispatchEvent(
            new ErrorEvent('error', { message: 'window boom', error: new Error('window boom') }),
        );
        const rejection = new Event('unhandledrejection') as Event & { reason?: unknown };
        rejection.reason = new Error('promise boom');
        window.dispatchEvent(rejection);

        await vi.advanceTimersByTimeAsync(2500);

        const kinds = bodyOf(0).items.map((i: { kind: string }) => i.kind);
        expect(kinds).toContain('error');
        expect(kinds).toContain('unhandledrejection');
        cleanup();
    });

    it('sends the bearer token when signed in, and omits it when not', async () => {
        reportClientError({ kind: 'error', message: 'anon' });
        await vi.advanceTimersByTimeAsync(2500);
        expect(fetchMock.mock.calls[0][1].headers.Authorization).toBeUndefined();

        __resetClientErrorsForTests();
        // The token lives in memory now, not storage, so this is how a signed-in state is made.
        tokenStore.set('header.payload.sig');
        reportClientError({ kind: 'error', message: 'authed' });
        await vi.advanceTimersByTimeAsync(2500);
        expect(fetchMock.mock.calls[1][1].headers.Authorization).toBe('Bearer header.payload.sig');
    });
});
