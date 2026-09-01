import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api, type Paginated } from '@/lib/api';

export interface SystemSetting {
    key: string;
    value: string;
    description: string;
    category: string;
    updatedAt: string;
}

export function useSettings() {
    return useQuery({
        queryKey: ['settings'],
        queryFn: async () => {
            const response = await api.get<Paginated<SystemSetting>>('/api/settings');
            return response.data.items;
        },
    });
}

export function useUpdateSetting() {
    const queryClient = useQueryClient();

    return useMutation({
        mutationFn: async ({ key, value }: { key: string; value: string }) => {
            const response = await api.post<{ success: boolean; message: string }>('/api/settings', {
                key,
                value,
            });
            return response.data;
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['settings'] });
        },
    });
}

// Email credentials are their own document and their own endpoints, because everything above is
// stored in plaintext and returned in full by GET /api/settings. There is no field here that could
// hold the key: the API never sends it back, and a form that repopulated the box would put the
// secret in every browser cache and every screen share.
export type EmailSettingSource = 'None' | 'Configuration' | 'Stored';

export interface EmailSettings {
    apiKeySet: boolean;
    apiKeySource: EmailSettingSource;
    fromAddress: string;
    fromAddressSource: EmailSettingSource;
    updatedAt?: string | null;
    updatedBy?: string | null;
    /** False when no provider module is registered, so nothing would be delivered. */
    providerRegistered: boolean;
}

export function useEmailSettings() {
    return useQuery({
        queryKey: ['settings', 'email'],
        queryFn: async () => {
            const response = await api.get<EmailSettings>('/api/settings/email');
            return response.data;
        },
    });
}

// Null leaves a field alone, empty string clears it. The form cannot show the current key, so it has
// no way to send it back unchanged, and treating an untouched field as "clear it" would wipe the
// credential every time somebody corrected the From address.
export function useUpdateEmailSettings() {
    const queryClient = useQueryClient();

    return useMutation({
        mutationFn: async (data: { apiKey?: string | null; fromAddress?: string | null }) => {
            const response = await api.put<EmailSettings>('/api/settings/email', data);
            return response.data;
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['settings', 'email'] });
        },
    });
}

export function useSendTestEmail() {
    return useMutation({
        mutationFn: async () => {
            const response = await api.post<{ sent: boolean; message: string }>(
                '/api/settings/email/test',
            );
            return response.data;
        },
    });
}
