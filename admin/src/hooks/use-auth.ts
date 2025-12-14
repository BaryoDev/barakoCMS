'use client';

import { useState, useEffect, useCallback } from 'react';
import { useRouter } from 'next/navigation';

interface AuthState {
    isAuthenticated: boolean;
    isLoading: boolean;
    token: string | null;
}

export function useAuth() {
    const router = useRouter();
    const [authState, setAuthState] = useState<AuthState>({
        isAuthenticated: false,
        isLoading: true,
        token: null,
    });

    useEffect(() => {
        const token = localStorage.getItem('barako_token');
        setAuthState({
            isAuthenticated: !!token,
            isLoading: false,
            token,
        });
    }, []);

    const logout = useCallback(() => {
        localStorage.removeItem('barako_token');
        setAuthState({
            isAuthenticated: false,
            isLoading: false,
            token: null,
        });
        router.push('/login');
    }, [router]);

    const requireAuth = useCallback(() => {
        if (!authState.isLoading && !authState.isAuthenticated) {
            router.push('/login');
        }
    }, [authState.isLoading, authState.isAuthenticated, router]);

    return {
        ...authState,
        logout,
        requireAuth,
    };
}
