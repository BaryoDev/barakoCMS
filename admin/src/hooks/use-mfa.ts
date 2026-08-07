'use client';

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api';

export interface MfaStatus {
    enabled: boolean;
}

export interface MfaSetup {
    /** Base32 secret, shown once so it can be typed into an authenticator manually. */
    secret: string;
    /** otpauth:// URI to render as a QR code. */
    otpauthUri: string;
}

export interface MfaEnableResult {
    message: string;
    /** One-time recovery codes. Returned once, on enable, and never retrievable again. */
    recoveryCodes: string[];
}

const STATUS_KEY = ['mfa', 'status'];

export function useMfaStatus() {
    return useQuery({
        queryKey: STATUS_KEY,
        queryFn: async () => {
            const { data } = await api.get<MfaStatus>('/api/auth/mfa/status');
            return data;
        },
    });
}

/** Starts (or restarts) enrollment. The secret is not active until enable succeeds. */
export function useMfaSetup() {
    return useMutation({
        mutationFn: async () => {
            const { data } = await api.post<MfaSetup>('/api/auth/mfa/setup', {});
            return data;
        },
    });
}

export function useMfaEnable() {
    const queryClient = useQueryClient();
    return useMutation({
        mutationFn: async (code: string) => {
            const { data } = await api.post<MfaEnableResult>('/api/auth/mfa/enable', { code });
            return data;
        },
        onSuccess: () => queryClient.invalidateQueries({ queryKey: STATUS_KEY }),
    });
}

export function useMfaDisable() {
    const queryClient = useQueryClient();
    return useMutation({
        mutationFn: async (code: string) => {
            const { data } = await api.post<{ message: string }>('/api/auth/mfa/disable', { code });
            return data;
        },
        onSuccess: () => queryClient.invalidateQueries({ queryKey: STATUS_KEY }),
    });
}
