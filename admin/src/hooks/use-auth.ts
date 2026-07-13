'use client';

import { useCallback, useMemo, useSyncExternalStore } from 'react';
import { useRouter } from 'next/navigation';
import { useMutation } from '@tanstack/react-query';
import { api, subscribeToAuth, tokenStore } from '@/lib/api';

interface LoginResponse {
    token: string;
    expiry: string;
    refreshToken: string;
    refreshTokenExpiry: string;
}

export interface SessionUser {
    userId?: string;
    username?: string;
    roles: string[];
}

function decodeSession(token: string | null): SessionUser | null {
    if (!token) return null;
    try {
        const payload = JSON.parse(atob(token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/')));
        const roleClaim = payload['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'] ?? payload.role;
        return {
            userId: payload.UserId,
            username: payload.Username,
            roles: Array.isArray(roleClaim) ? roleClaim : roleClaim ? [roleClaim] : [],
        };
    } catch {
        return null;
    }
}

const emptySubscribe = () => () => {};

export function useAuth() {
    const router = useRouter();

    // False during SSR and hydration, true after — replaces a mount effect.
    const hydrated = useSyncExternalStore(
        emptySubscribe,
        () => true,
        () => false
    );
    const token = useSyncExternalStore(
        subscribeToAuth,
        () => tokenStore.token,
        () => null
    );

    const user = useMemo(() => decodeSession(token), [token]);
    const isLoading = !hydrated;

    const logout = useCallback(async () => {
        try {
            await api.post('/api/auth/logout');
        } catch {
            // Token may already be expired; clearing locally is what matters.
        }
        tokenStore.clear();
        router.push('/login');
    }, [router]);

    const requireAuth = useCallback(() => {
        if (!isLoading && !user) {
            router.push('/login');
        }
    }, [isLoading, user, router]);

    return {
        isAuthenticated: !!user,
        isLoading,
        user,
        logout,
        requireAuth,
    };
}

export function useLogin() {
    return useMutation({
        mutationFn: async (credentials: { username: string; password: string }) => {
            const { data } = await api.post<LoginResponse>('/api/auth/login', credentials);
            tokenStore.set(data.token, data.refreshToken);
            return data;
        },
    });
}
