import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import React from 'react';
import { renderHook, act, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

/**
 * The MFA challenge is a 200 response that carries no tokens. The bug this guards against is treating
 * it like a normal login: storing an empty token looks like a session to the rest of the app, so the
 * user lands "signed in" holding a token the API rejects, with no way back to the login form.
 */
vi.mock('@/lib/api', async () => {
    const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api');
    return { ...actual, api: { post: vi.fn() } };
});

const { api, tokenStore } = await import('@/lib/api');
const { useLogin, useVerifyMfa } = await import('./use-auth');

function wrapper({ children }: { children: React.ReactNode }) {
    const client = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
    return React.createElement(QueryClientProvider, { client }, children);
}

describe('login with MFA', () => {
    beforeEach(() => {
        localStorage.clear();
        vi.mocked(api.post).mockReset();
    });
    afterEach(() => localStorage.clear());

    it('does not store a token when the response is an MFA challenge', async () => {
        vi.mocked(api.post).mockResolvedValue({
            data: { token: '', refreshToken: '', requiresMfa: true, mfaChallengeToken: 'challenge-abc' },
        });

        const { result } = renderHook(() => useLogin(), { wrapper });
        act(() => result.current.mutate({ username: 'u', password: 'p' }));
        await waitFor(() => expect(result.current.isSuccess).toBe(true));

        expect(tokenStore.token).toBeNull();
        expect(result.current.data?.requiresMfa).toBe(true);
        expect(result.current.data?.mfaChallengeToken).toBe('challenge-abc');
    });

    it('stores the token on a normal login', async () => {
        vi.mocked(api.post).mockResolvedValue({ data: { token: 'real-token', refreshToken: 'real-refresh' } });

        const { result } = renderHook(() => useLogin(), { wrapper });
        act(() => result.current.mutate({ username: 'u', password: 'p' }));
        await waitFor(() => expect(result.current.isSuccess).toBe(true));

        expect(tokenStore.token).toBe('real-token');
    });

    it('stores the token after the second factor verifies', async () => {
        vi.mocked(api.post).mockResolvedValue({ data: { token: 'post-mfa', refreshToken: 'post-mfa-refresh' } });

        const { result } = renderHook(() => useVerifyMfa(), { wrapper });
        act(() => result.current.mutate({ challengeToken: 'challenge-abc', code: '123456' }));
        await waitFor(() => expect(result.current.isSuccess).toBe(true));

        expect(api.post).toHaveBeenCalledWith('/api/auth/mfa/verify', {
            challengeToken: 'challenge-abc',
            code: '123456',
        });
        expect(tokenStore.token).toBe('post-mfa');
    });
});
