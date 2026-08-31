'use client';

import { useCallback, useEffect, useMemo, useState, useSyncExternalStore } from 'react';
import { useRouter } from 'next/navigation';
import { useMutation } from '@tanstack/react-query';
import { api, ensureSession, subscribeToAuth, tokenStore } from '@/lib/api';

interface LoginResponse {
    token: string;
    expiry: string;
    refreshToken: string;
    refreshTokenExpiry: string;
    /** True when the password was right but the account needs a second factor. No tokens are issued. */
    requiresMfa?: boolean;
    /** Short-lived grant to complete the second step at /api/auth/mfa/verify. */
    mfaChallengeToken?: string;
    /** True when the device needs email approval instead (a separate second step). */
    requiresDeviceApproval?: boolean;
    message?: string;
    email?: string;
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

    // The access token is in memory, so a reload starts with none and the refresh cookie is what
    // carries the session. Until that one silent refresh has settled we do not know whether there
    // is a session, and treating "no token yet" as "signed out" would redirect to the login page on
    // every reload.
    const [bootstrapped, setBootstrapped] = useState(false);
    useEffect(() => {
        let cancelled = false;
        ensureSession().finally(() => {
            if (!cancelled) setBootstrapped(true);
        });
        return () => {
            cancelled = true;
        };
    }, []);
    const token = useSyncExternalStore(
        subscribeToAuth,
        () => tokenStore.token,
        () => null
    );

    const user = useMemo(() => decodeSession(token), [token]);
    const isLoading = !hydrated || !bootstrapped;

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
            // A second-factor challenge is a successful response that carries NO tokens. Storing the
            // empty string here would look like a session and lock the user out of the UI, so only
            // persist when a real token came back; the caller drives the second step.
            if (data.token) {
                tokenStore.set(data.token);
            }
            return data;
        },
    });
}

/**
 * Completes a device approval: the emailed code, in exchange for tokens.
 *
 * The server answered `requiresDeviceApproval` and emailed a code, and the admin had nowhere to put
 * it, so turning on DeviceTrust__Enforce locked every administrator out of their own instance with
 * no way back in. The quickstart advertises that setting.
 *
 * It can chain: a correct email code on an account with MFA enabled returns `requiresMfa` and a
 * challenge token rather than a session, because possession of a mailbox is a first factor and
 * cannot stand in for the enrolled second one.
 */
export function useVerifyDeviceCode() {
    return useMutation({
        mutationFn: async (input: { email: string; code: string }) => {
            const { data } = await api.post<LoginResponse>('/api/auth/otp/verify', input);
            // No tokens when a second factor is still owed; the caller moves to the MFA step.
            if (data.token) tokenStore.set(data.token);
            return data;
        },
    });
}

/** Completes a two-step sign-in: challenge token + a TOTP or recovery code, in exchange for tokens. */
export function useVerifyMfa() {
    return useMutation({
        mutationFn: async (input: { challengeToken: string; code: string }) => {
            const { data } = await api.post<LoginResponse>('/api/auth/mfa/verify', input);
            tokenStore.set(data.token);
            return data;
        },
    });
}
